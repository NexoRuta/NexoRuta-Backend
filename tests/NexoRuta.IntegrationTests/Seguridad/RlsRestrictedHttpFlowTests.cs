using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Domain.Administracion;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.Infrastructure.Administracion;
using NexoRuta.Infrastructure.Persistence;
using Npgsql;

namespace NexoRuta.IntegrationTests.Seguridad;

public sealed class RlsRestrictedHttpFlowTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task RestrictedCommerceHttpCanCreateShipmentAndSeeItAfterChosenOperatorSuspension()
    {
        var database = await postgres.CreateDatabaseAsync();
        const string commerceEmail = "rls-http-commerce@example.test";
        Guid operatorId;
        await using (var owner = database.CreateDbContext())
        {
            await AuthenticationTestData.Seeder(owner,
                new AccesoInicial(commerceEmail, "Chosen operator", "Test commerce", "rls-http-operator@example.test"))
                .SeedAsync();
            operatorId = await owner.Operadores.Select(x => x.Id).SingleAsync();
        }

        var runtime = await RestrictApiAsync(database);
        using var api = new IntegrationApiFactory(runtime, settings: ApiSettings());
        using var client = api.CreateHttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, commerceEmail));

        using var created = await client.PostAsJsonAsync("/api/envios", new
        {
            OperadorId = operatorId,
            DestinatarioNombre = "Ada Lovelace",
            Direccion = "18 de Julio 123",
            CodigoBulto = "RLS-1",
            PesoGramos = 500m,
            LargoCentimetros = 20m,
            AnchoCentimetros = 10m,
            AltoCentimetros = 5m
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        var shipmentId = createdBody.GetProperty("id").GetGuid();

        await using (var owner = database.CreateDbContext())
        {
            var selectedOperator = await owner.Operadores.SingleAsync(x => x.Id == operatorId);
            selectedOperator.CambiarEstado(EstadoOrganizacion.Suspendida);
            await owner.SaveChangesAsync();
        }

        using var listed = await client.GetAsync("/api/envios");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var shipments = await listed.Content.ReadFromJsonAsync<JsonElement[]>();
        var historical = Assert.Single(shipments!, x => x.GetProperty("id").GetGuid() == shipmentId);
        Assert.Equal("Chosen operator", historical.GetProperty("operadorNombre").GetString());
    }

    [Fact]
    public async Task RestrictedCommerceCanRemoveRolePermissionsAndAssignProtectedRole()
    {
        var database = await postgres.CreateDatabaseAsync();
        const string commerceEmail = "rls-http-role-owner@example.test";
        Guid commerceId;
        Guid ownerRoleId;
        Guid employeeAccessId;
        await using (var owner = database.CreateDbContext())
        {
            await AuthenticationTestData.Seeder(owner,
                new AccesoInicial(commerceEmail, "Role test operator", "Role test commerce", "rls-role-operator@example.test"))
                .SeedAsync();
            var access = await owner.AccesosUsuario.SingleAsync(x => x.ComercioId != null);
            commerceId = access.ComercioId!.Value;
            ownerRoleId = access.RolId!.Value;
            var permissionId = await owner.Permisos.Where(x => x.Codigo == "configuracion.gestionar")
                .Select(x => x.Id).SingleAsync();
            if (!await owner.RolesPermisos.AnyAsync(x => x.RolId == ownerRoleId && x.PermisoId == permissionId))
                owner.RolesPermisos.Add(new RolPermiso(ownerRoleId, permissionId));
            var employee = new Usuario("rls-http-employee@example.test", AuthenticationTestData.Hash("rls-http-employee@example.test"));
            var employeeAccess = new AccesoUsuario(employee.Id, null, commerceId);
            owner.AddRange(employee, employeeAccess);
            await owner.SaveChangesAsync();
            employeeAccessId = employeeAccess.Id;
        }

        var runtime = await RestrictApiAsync(database);
        using var api = new IntegrationApiFactory(runtime, settings: ApiSettings());
        using var client = api.CreateHttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, commerceEmail));

        using var createdRole = await client.PostAsJsonAsync("/api/roles", new { Codigo = "rls-editable", Nombre = "RLS editable" });
        Assert.Equal(HttpStatusCode.Created, createdRole.StatusCode);
        var roleId = (await createdRole.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var grants = await client.PutAsJsonAsync($"/api/roles/{roleId}/permisos", new { Permisos = new[] { "envios.consultar" } });
        Assert.Equal(HttpStatusCode.NoContent, grants.StatusCode);
        using var grantedRoles = await client.GetAsync("/api/roles");
        var grantedRole = (await grantedRoles.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()
            .Single(x => x.GetProperty("id").GetGuid() == roleId);
        Assert.Contains("envios.consultar", grantedRole.GetProperty("permisos").EnumerateArray().Select(x => x.GetString()));
        using var removeGrants = await client.PutAsJsonAsync($"/api/roles/{roleId}/permisos", new { Permisos = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.NoContent, removeGrants.StatusCode);
        using var removedRoles = await client.GetAsync("/api/roles");
        var removedRole = (await removedRoles.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()
            .Single(x => x.GetProperty("id").GetGuid() == roleId);
        Assert.Empty(removedRole.GetProperty("permisos").EnumerateArray());
        using var deleteRole = await client.DeleteAsync($"/api/roles/{roleId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteRole.StatusCode);

        using var assignProtectedRole = await client.PutAsJsonAsync(
            $"/api/roles/accesos/{employeeAccessId}", new { RolId = ownerRoleId });
        Assert.Equal(HttpStatusCode.NoContent, assignProtectedRole.StatusCode);
        await using var verify = database.CreateDbContext();
        Assert.Equal(ownerRoleId, (await verify.AccesosUsuario.SingleAsync(x => x.Id == employeeAccessId)).RolId);
        Assert.True(await verify.Roles.AsNoTracking().AnyAsync(x => x.Id == ownerRoleId));
    }

    private static Dictionary<string, string?> ApiSettings()
    {
        var settings = AuthenticationTestData.Settings();
        settings["DatabaseInitialization:SeedData"] = "false";
        return settings;
    }

    private static async Task<string> LoginAsync(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            Email = email,
            Password = AuthenticationTestData.Password
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    private static async Task<IntegrationDatabase> RestrictApiAsync(IntegrationDatabase database)
    {
        var role = $"rls_http_{Guid.NewGuid():N}";
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using (var owner = new NpgsqlConnection(database.ConnectionString))
        {
            await owner.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE ROLE {role} LOGIN PASSWORD '{password}' NOSUPERUSER NOBYPASSRLS; GRANT nexoruta_api TO {role};", owner);
            await create.ExecuteNonQueryAsync();
        }
        var connectionString = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Username = role,
            Password = password,
            Pooling = true,
            MaxPoolSize = 4
        }.ConnectionString;
        var restrictedDatabase = new IntegrationDatabase(connectionString);
        return restrictedDatabase;
    }
}
