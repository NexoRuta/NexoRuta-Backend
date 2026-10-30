using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Administracion.Dtos;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Administracion;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.IntegrationTests.Fixtures;
using Npgsql;

namespace NexoRuta.IntegrationTests.Envios;

public sealed class OperadoresDisponiblesHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task ImportOperatorCatalogIncludesOnlyThisCommerceHistoricalInactiveOperatorsWithoutConsultPermission()
    {
        var database = await postgres.CreateDatabaseAsync();
        Guid commerceAccessId;
        Guid originalOperatorId;
        Guid operatorAccessId;
        await using (var db = database.CreateDbContext())
        {
            await AuthenticationTestData.Seeder(db,
                new AccesoInicial("owner@example.test", "Alpha operator", "Test commerce", "operator@example.test"))
                .SeedAsync();
            await ReglasEnvioTestData.SembrarInicialesAsync(db);
            var commerceAccess = await db.AccesosUsuario.Where(x => x.ComercioId != null).SingleAsync();
            commerceAccessId = commerceAccess.Id;
            originalOperatorId = await db.Operadores.Select(x => x.Id).SingleAsync();
            operatorAccessId = await db.AccesosUsuario.Where(x => x.OperadorId == originalOperatorId).Select(x => x.Id).SingleAsync();

            var consultPermissionId = await db.Permisos.Where(x => x.Codigo == "envios.consultar").Select(x => x.Id).SingleAsync();
            var consultGrant = await db.RolesPermisos.SingleAsync(x => x.RolId == commerceAccess.RolId && x.PermisoId == consultPermissionId);
            db.RolesPermisos.Remove(consultGrant);
            await db.SaveChangesAsync();
        }

        await using (var db = database.CreateDbContext())
            await AuthenticationTestData.Seeder(db,
                new AccesoInicial("owner-two@example.test", "Beta operator", "Second commerce", "operator-two@example.test"))
                .SeedAsync();

        var apiSettings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = await CreateRestrictedApiConnectionAsync(database),
            ["DatabaseInitialization:ApplyMigrations"] = "false",
            ["DatabaseInitialization:SeedData"] = "false"
        };
        using var api = new IntegrationApiFactory(database, environment: "Development", settings: apiSettings,
            replaceDbContext: false);
        using var client = api.CreateHttpClient();
        await client.SetBearerForAccessAsync(database, commerceAccessId);

        using var import = await ImportAsync(client, originalOperatorId, "HISTORIC-1");
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);

        await using (var db = database.CreateDbContext())
        {
            var inactiveOperator = await db.Operadores.SingleAsync(x => x.Id == originalOperatorId);
            inactiveOperator.CambiarEstado(EstadoOrganizacion.Suspendida);
            await db.SaveChangesAsync();
        }

        using var catalogResponse = await client.GetAsync("/api/operadores/importacion-envios");
        var catalogJson = await catalogResponse.Content.ReadAsStringAsync();
        Assert.True(catalogResponse.StatusCode == HttpStatusCode.OK,
            $"Expected 200 from the import operator catalog, got {(int)catalogResponse.StatusCode}.");
        using var catalogDocument = JsonDocument.Parse(catalogJson);
        var catalog = await catalogResponse.Content.ReadFromJsonAsync<OperadorDisponibleParaImportacion[]>();
        Assert.NotNull(catalog);
        var original = Assert.Single(catalog, x => x.OperadorId == originalOperatorId);
        Assert.Equal("Alpha operator", original.Nombre);
        Assert.False(original.Activo);
        Assert.Equal(3, catalogDocument.RootElement[0].EnumerateObject().Count());

        using var noReadPermission = await client.GetAsync("/api/envios");
        Assert.Equal(HttpStatusCode.Forbidden, noReadPermission.StatusCode);

        await using (var db = database.CreateDbContext())
        {
            var secondAccess = await db.AccesosUsuario.Where(x => x.ComercioId != null && x.Id != commerceAccessId).SingleAsync();
            await client.SetBearerForAccessAsync(database, secondAccess.Id);
        }
        using var secondCommerceResponse = await client.GetAsync("/api/operadores/importacion-envios");
        Assert.Equal(HttpStatusCode.OK, secondCommerceResponse.StatusCode);
        var secondCatalog = await secondCommerceResponse.Content.ReadFromJsonAsync<OperadorDisponibleParaImportacion[]>();
        Assert.NotNull(secondCatalog);
        Assert.DoesNotContain(secondCatalog, x => x.OperadorId == originalOperatorId);
        Assert.All(secondCatalog, x => Assert.True(x.Activo));

        await client.SetBearerForAccessAsync(database, operatorAccessId);
        using var operatorActor = await client.GetAsync("/api/operadores/importacion-envios");
        Assert.Equal(HttpStatusCode.Forbidden, operatorActor.StatusCode);
    }

    [Fact]
    public async Task SuspendedOperatorsAreHiddenAndManualSelectionCannotCreateShipment()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using (var db = database.CreateDbContext())
        {
            await AuthenticationTestData.Seeder(db,
                new AccesoInicial("owner@example.test", "Active operator", "Test commerce", "operator@example.test"))
                .SeedAsync();
            var suspended = new Operador("Suspended operator");
            suspended.CambiarEstado(EstadoOrganizacion.Suspendida);
            db.Operadores.Add(suspended);
            await db.SaveChangesAsync();
        }
        using var api = new IntegrationApiFactory(database, settings: new Dictionary<string, string?>
        {
            ["DatabaseInitialization:SeedData"] = "false"
        });
        using var client = api.CreateHttpClient();
        var commerceToken = await LoginAsync(client, "owner@example.test");
        using var catalogRequest = new HttpRequestMessage(HttpMethod.Get, "/api/operadores");
        catalogRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", commerceToken);
        using var catalogResponse = await client.SendAsync(catalogRequest);
        Assert.Equal(HttpStatusCode.OK, catalogResponse.StatusCode);
        var catalog = await catalogResponse.Content.ReadFromJsonAsync<OperadorDisponible[]>();
        Assert.Equal(new[] { "Active operator" }, catalog!.Select(x => x.Nombre));

        Guid suspendedId;
        await using (var db = database.CreateDbContext())
            suspendedId = await db.Operadores.Where(x => x.Estado == EstadoOrganizacion.Suspendida).Select(x => x.Id).SingleAsync();
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/envios")
        {
            Content = JsonContent.Create(new
            {
                OperadorId = suspendedId, DestinatarioNombre = "Ana", DestinatarioEmail = "destinatario@example.test", Direccion = "Rivera 123", CodigoBulto = "B-001",
                PesoGramos = 100m, LargoCentimetros = 10m, AnchoCentimetros = 10m, AltoCentimetros = 10m
            })
        };
        createRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", commerceToken);
        using var rejected = await client.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        await using var verify = database.CreateDbContext();
        Assert.Empty(await verify.Envios.ToListAsync());
        Assert.Empty(await verify.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task CatalogoGlobal_UsaUnaSolaRutaYAutorizaSoloComercios()
    {
        const string route = "api/operadores";
        var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        await AuthenticationTestData.Seeder(db,
            new AccesoInicial("owner@example.test", "Alpha operator", "Test commerce", "operator@example.test"))
            .SeedAsync();
        db.Operadores.Add(new Operador("Beta operator without users"));
        await db.SaveChangesAsync();
        var comercio = await db.AccesosUsuario.Where(x => x.ComercioId != null).SingleAsync();
        var operador = await db.AccesosUsuario.Where(x => x.OperadorId != null).SingleAsync();

        using var api = new IntegrationApiFactory(database, "Development", settings: new Dictionary<string, string?>
        {
            ["DatabaseInitialization:SeedData"] = "false"
        });
        using var client = api.CreateHttpClient();
        using var anonymous = await client.GetAsync(route);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var eliminada = await client.GetAsync("api/comercio/operadores");
        Assert.Equal(HttpStatusCode.NotFound, eliminada.StatusCode);

        var operatorEmail = (await db.Usuarios.SingleAsync(x => x.Id == operador.UsuarioId)).Email;
        var commerceEmail = (await db.Usuarios.SingleAsync(x => x.Id == comercio.UsuarioId)).Email;
        var operatorToken = await LoginAsync(client, operatorEmail);
        var commerceToken = await LoginAsync(client, commerceEmail);

        using var operatorRequest = new HttpRequestMessage(HttpMethod.Get, route);
        operatorRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", operatorToken);
        using var forbidden = await client.SendAsync(operatorRequest);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using var commerceRequest = new HttpRequestMessage(HttpMethod.Get, route);
        commerceRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", commerceToken);
        using var available = await client.SendAsync(commerceRequest);
        Assert.Equal(HttpStatusCode.OK, available.StatusCode);
        var catalog = await available.Content.ReadFromJsonAsync<OperadorDisponible[]>();
        Assert.NotNull(catalog);
        Assert.Equal(new[] { "Alpha operator", "Beta operator without users" }, catalog.Select(x => x.Nombre));
        Assert.All(catalog, item => Assert.NotEqual(Guid.Empty, item.OperadorId));
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

    private static Task<HttpResponseMessage> ImportAsync(HttpClient client, Guid operatorId, string reference)
    {
        const string csvHeader = "referenciaExterna,nombreDestinatario,emailDestinatario,direccion,codigoBulto,pesoGramos,largoCentimetros,anchoCentimetros,altoCentimetros";
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(
            $"{csvHeader}\r\n{reference},Ada Lovelace,ada@example.test,18 de Julio 123,B-001,1250,30,20,10")), "archivo", "envios.csv");
        form.Add(new StringContent(operatorId.ToString("D")), "operadorId");
        return client.PostAsync("/api/envios/importaciones", form);
    }

    private static async Task<string> CreateRestrictedApiConnectionAsync(IntegrationDatabase database)
    {
        var role = $"cu07_catalog_{Guid.NewGuid():N}";
        var password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var create = new NpgsqlCommand(
            $"CREATE ROLE \"{role}\" LOGIN INHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS PASSWORD '{password}'", connection))
            await create.ExecuteNonQueryAsync();
        await using (var grant = new NpgsqlCommand($"GRANT nexoruta_api TO \"{role}\"", connection))
            await grant.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Username = role,
            Password = password,
            Pooling = false
        }.ConnectionString;
    }
}
