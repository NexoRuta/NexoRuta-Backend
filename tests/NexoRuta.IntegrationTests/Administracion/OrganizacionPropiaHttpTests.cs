using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Domain.Administracion;
using NexoRuta.IntegrationTests.Fixtures;

namespace NexoRuta.IntegrationTests.Administracion;

public sealed class OrganizacionPropiaHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData("comercios")]
    [InlineData("operadores")]
    public async Task OwnerReadsAndUpdatesOnlyNameWithVersion(string resource)
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = Factory(database);
        using var client = factory.CreateHttpClient();
        var email = $"owner-{Guid.NewGuid():N}@example.test";
        var organization = await CreateOwnedOrganizationAsync(database, client, resource, email);

        using var read = await client.GetAsync("/api/organizacion/actual");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var initial = (await read.Content.ReadFromJsonAsync<JsonElement>()).Clone();
        Assert.Equal(organization.Id, initial.GetProperty("id").GetGuid());
        Assert.Equal("Original name", initial.GetProperty("nombre").GetString());
        Assert.True(initial.GetProperty("esPropietario").GetBoolean());
        Assert.True(initial.GetProperty("tieneDuenoRegistrado").GetBoolean());

        using var updated = await client.PutAsJsonAsync("/api/organizacion/actual", new
        {
            Nombre = "Updated name",
            Version = initial.GetProperty("version").GetInt64()
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var result = await updated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Updated name", result.GetProperty("nombre").GetString());
        Assert.Equal(initial.GetProperty("version").GetInt64() + 1, result.GetProperty("version").GetInt64());

        using var tooLong = await client.PutAsJsonAsync("/api/organizacion/actual", new
        {
            Nombre = new string('x', 161),
            Version = result.GetProperty("version").GetInt64()
        });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        using var tampered = await client.PutAsJsonAsync("/api/organizacion/actual", new
        {
            Nombre = "Must not persist",
            Version = result.GetProperty("version").GetInt64(),
            Id = Guid.NewGuid(),
            UsuarioId = Guid.NewGuid(),
            EsPropietario = false,
            Activo = false,
            RolId = Guid.NewGuid(),
            Permisos = new[] { "platform.organizations.manage" }
        });
        Assert.Equal(HttpStatusCode.BadRequest, tampered.StatusCode);

        using var reread = await client.GetAsync("/api/organizacion/actual");
        var persisted = await reread.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(organization.Id, persisted.GetProperty("id").GetGuid());
        Assert.Equal("Updated name", persisted.GetProperty("nombre").GetString());
        Assert.True(persisted.GetProperty("esPropietario").GetBoolean());
        var currentUser = await client.GetFromJsonAsync<JsonElement>("/api/usuarios/actual");
        Assert.Equal("Updated name", currentUser.GetProperty(resource == "comercios" ? "comercioNombre" : "operadorNombre").GetString());
        await using var finalDb = database.CreateDbContext();
        var access = await finalDb.AccesosUsuario.SingleAsync(x => x.UsuarioId == organization.UserId);
        Assert.Equal(organization.RoleId, access.RolId);
        Assert.Equal(0, access.VersionSesion);
        Assert.Equal(1, await OwnershipCountAsync(database, organization.UserId));
    }

    [Fact]
    public async Task ActiveEmployeeCanReadButCannotEditAndForeignAndPlatformSessionsCannotUseOrganizationContext()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = Factory(database);
        using var client = factory.CreateHttpClient();
        var owner = await CreateOwnedOrganizationAsync(database, client, "comercios", $"owner-{Guid.NewGuid():N}@example.test");

        var employeeEmail = $"employee-{Guid.NewGuid():N}@example.test";
        await using (var db = database.CreateDbContext())
        {
            var role = await db.Roles.SingleAsync(x => x.Id == owner.RoleId);
            var employee = new Usuario(employeeEmail, AuthenticationTestData.Hash(employeeEmail));
            db.AddRange(employee, new AccesoUsuario(employee.Id, null, owner.Id, role));
            await db.SaveChangesAsync();
        }
        await LoginAsync(client, employeeEmail);
        var employeeRead = await client.GetFromJsonAsync<JsonElement>("/api/organizacion/actual");
        Assert.Equal(owner.Id, employeeRead.GetProperty("id").GetGuid());
        Assert.False(employeeRead.GetProperty("esPropietario").GetBoolean());
        Assert.True(employeeRead.GetProperty("tieneDuenoRegistrado").GetBoolean());
        using var employeeEdit = await client.PutAsJsonAsync("/api/organizacion/actual", new { Nombre = "Employee edit", Version = 0 });
        Assert.Equal(HttpStatusCode.Forbidden, employeeEdit.StatusCode);

        var foreign = await CreateOwnedOrganizationAsync(database, client, "operadores", $"foreign-{Guid.NewGuid():N}@example.test");
        using var foreignEdit = await client.PutAsJsonAsync("/api/organizacion/actual", new
        {
            Nombre = "Foreign edit",
            Version = 0,
            OperadorId = foreign.Id
        });
        Assert.Equal(HttpStatusCode.BadRequest, foreignEdit.StatusCode);
        var scoped = await client.GetFromJsonAsync<JsonElement>("/api/organizacion/actual");
        Assert.Equal(foreign.Id, scoped.GetProperty("id").GetGuid());

        await using (var db = database.CreateDbContext())
            await AuthenticationTestData.PlatformRepository(db).InicializarAsync($"platform-{Guid.NewGuid():N}@example.test", AuthenticationTestData.Password);
        var platformEmail = await ReadPlatformEmailAsync(database);
        using var platformFactory = Factory(database);
        using var platformClient = platformFactory.CreateHttpClient();
        await LoginAsync(platformClient, platformEmail);
        using var platformRead = await platformClient.GetAsync("/api/organizacion/actual");
        using var platformEdit = await platformClient.PutAsJsonAsync("/api/organizacion/actual", new { Nombre = "Platform edit", Version = 0 });
        Assert.Equal(HttpStatusCode.Forbidden, platformRead.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, platformEdit.StatusCode);

        using var anonymousFactory = Factory(database);
        using var anonymousClient = anonymousFactory.CreateHttpClient();
        using var anonymous = await anonymousClient.GetAsync("/api/organizacion/actual");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task OrganizationWithoutRegisteredOwnerCanBeReadButEditExplainsMissingOwner()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        Guid organizationId;
        await using (var db = database.CreateDbContext())
        {
            var access = await db.AccesosUsuario.SingleAsync(x => x.ComercioId != null);
            organizationId = access.ComercioId!.Value;
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"AccesosUsuario\" SET \"EsPropietario\" = FALSE WHERE \"ComercioId\" = {organizationId}");
            db.ChangeTracker.Clear();
            access = await db.AccesosUsuario.SingleAsync(x => x.ComercioId == organizationId);
            Assert.False(access.EsPropietario);
            await client.SetBearerForAccessAsync(database, access.Id);
        }

        var read = await client.GetFromJsonAsync<JsonElement>("/api/organizacion/actual");
        Assert.Equal(organizationId, read.GetProperty("id").GetGuid());
        Assert.False(read.GetProperty("esPropietario").GetBoolean());
        Assert.False(read.GetProperty("tieneDuenoRegistrado").GetBoolean());
        using var update = await client.PutAsJsonAsync("/api/organizacion/actual", new { Nombre = "Should not change", Version = 0 });
        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
        Assert.Contains("dueño registrado", await update.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        await using var unchangedDb = database.CreateDbContext();
        Assert.Equal("Integration commerce", (await unchangedDb.Comercios.SingleAsync(x => x.Id == organizationId)).Nombre);
        Assert.False((await unchangedDb.AccesosUsuario.SingleAsync(x => x.ComercioId == organizationId)).EsPropietario);
    }

    [Fact]
    public async Task ConcurrentUpdatesWithTheSameVersionReturnOneConflictWithoutLostUpdate()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = Factory(database);
        using var client = factory.CreateHttpClient();
        var owner = await CreateOwnedOrganizationAsync(database, client, "comercios", $"race-{Guid.NewGuid():N}@example.test");
        using var first = new HttpRequestMessage(HttpMethod.Put, "/api/organizacion/actual")
        {
            Content = JsonContent.Create(new { Nombre = "First writer", Version = 0 })
        };
        using var second = new HttpRequestMessage(HttpMethod.Put, "/api/organizacion/actual")
        {
            Content = JsonContent.Create(new { Nombre = "Second writer", Version = 0 })
        };
        var responses = await Task.WhenAll(client.SendAsync(first), client.SendAsync(second));
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, responses.Select(x => x.StatusCode).Order());
        await using var db = database.CreateDbContext();
        var organization = await db.Comercios.SingleAsync(x => x.Id == owner.Id);
        Assert.Contains(organization.Nombre, new[] { "First writer", "Second writer" });
        Assert.Equal(1, db.Entry(organization).Property<long>("Version").CurrentValue);
        Assert.Equal(1, await OwnershipCountAsync(database, owner.UserId));
    }

    private static IntegrationApiFactory Factory(IntegrationDatabase db)
        => new(db, settings: new Dictionary<string, string?> { ["DatabaseInitialization:SeedData"] = "false" });

    private static async Task<OwnedOrganization> CreateOwnedOrganizationAsync(IntegrationDatabase database, HttpClient client, string resource, string email)
    {
        const string password = AuthenticationTestData.Password;
        using var registered = await client.PostAsJsonAsync("/api/auth/register", new { Email = email, Password = password });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        await LoginAsync(client, email);
        var identity = await client.GetFromJsonAsync<JsonElement>("/api/usuarios/actual");
        var userId = identity.GetProperty("usuarioId").GetGuid();
        using var created = await client.PostAsJsonAsync($"/api/alta-propia/{resource}", new { Nombre = "Original name" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var json = await created.Content.ReadFromJsonAsync<JsonElement>();
        var organization = json.GetProperty("organizacion");
        await LoginAsync(client, email);
        await using var db = database.CreateDbContext();
        var access = await db.AccesosUsuario.SingleAsync(x => x.UsuarioId == userId);
        return new OwnedOrganization(organization.GetProperty("id").GetGuid(), userId, access.Id, access.RolId!.Value);
    }

    private static async Task LoginAsync(HttpClient client, string email)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { Email = email, Password = AuthenticationTestData.Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static async Task<int> OwnershipCountAsync(IntegrationDatabase database, Guid userId)
    {
        await using var db = database.CreateDbContext();
        return await db.AccesosUsuario.CountAsync(x => x.UsuarioId == userId && x.EsPropietario);
    }

    private static async Task<string> ReadPlatformEmailAsync(IntegrationDatabase database)
    {
        await using var db = database.CreateDbContext();
        return await db.AccesosPlataforma.Join(db.Usuarios, x => x.UsuarioId, x => x.Id, (_, user) => user.Email).SingleAsync();
    }

    private sealed record OwnedOrganization(Guid Id, Guid UserId, Guid AccessId, Guid RoleId);
}
