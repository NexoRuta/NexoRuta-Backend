using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Domain.Administracion;
using NexoRuta.IntegrationTests.Fixtures;

namespace NexoRuta.IntegrationTests.Administracion;

public sealed class SuspensionSessionHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task OrganizationNameEditAndPlatformStateChangeShareTheConcurrencyVersion()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = new IntegrationApiFactory(database, settings: new Dictionary<string, string?>
        {
            ["DatabaseInitialization:SeedData"] = "false"
        });
        using var ownerClient = factory.CreateHttpClient();
        var owner = await CreateOwnerAsync(database, ownerClient);
        await InitializePlatformAsync(database);
        using var platformClient = factory.CreateHttpClient();
        await LoginAsync(platformClient, "platform-suspension@example.test");

        using var edited = await ownerClient.PutAsJsonAsync("/api/organizacion/actual", new { Nombre = "Renamed before suspension", Version = 0L });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        using var staleSuspend = await platformClient.PutAsJsonAsync($"/api/admin/comercios/{owner.OrganizationId}/estado",
            new { Estado = "Suspendida", Motivo = "Support hold", Version = 0L });
        Assert.Equal(HttpStatusCode.Conflict, staleSuspend.StatusCode);

        await using var verify = database.CreateDbContext();
        var commerce = await verify.Comercios.SingleAsync(x => x.Id == owner.OrganizationId);
        Assert.Equal("Renamed before suspension", commerce.Nombre);
        Assert.Equal(EstadoOrganizacion.Activa, commerce.Estado);
        Assert.Empty(await verify.CambiosEstadoOrganizacion.ToListAsync());
        Assert.Equal(0, (await verify.AccesosUsuario.SingleAsync(x => x.UsuarioId == owner.UserId)).VersionSesion);
    }

    [Fact]
    public async Task SuspensionRevokesOldJwtAndOnlyAllowsARestrictedLoginStatusUntilReactivation()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = new IntegrationApiFactory(database, settings: new Dictionary<string, string?>
        {
            ["DatabaseInitialization:SeedData"] = "false"
        });
        using var ownerClient = factory.CreateHttpClient();
        var owner = await CreateOwnerAsync(database, ownerClient);
        await InitializePlatformAsync(database);
        using var platformClient = factory.CreateHttpClient();
        await LoginAsync(platformClient, "platform-suspension@example.test");
        var platformToken = platformClient.DefaultRequestHeaders.Authorization!.Parameter;

        using var suspended = await platformClient.PutAsJsonAsync($"/api/admin/comercios/{owner.OrganizationId}/estado",
            new { Estado = "Suspendida", Motivo = "Pausa por soporte", Version = 0L });
        Assert.Equal(HttpStatusCode.OK, suspended.StatusCode);
        Assert.Equal("Suspendida", (await suspended.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("estado").GetString());
        using var suspendedOrganizations = await platformClient.GetAsync("/api/admin/comercios?estado=Suspendida");
        Assert.Equal(HttpStatusCode.OK, suspendedOrganizations.StatusCode);
        var suspendedPage = await suspendedOrganizations.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, suspendedPage.GetProperty("total").GetInt32());
        Assert.Equal("Suspendida", suspendedPage.GetProperty("items")[0].GetProperty("estado").GetString());
        using var activeOrganizations = await platformClient.GetAsync("/api/admin/comercios?estado=Activa");
        Assert.Equal(0, (await activeOrganizations.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("total").GetInt32());
        using var invalidFilter = await platformClient.GetAsync("/api/admin/comercios?estado=Unknown");
        Assert.Equal(HttpStatusCode.BadRequest, invalidFilter.StatusCode);
        using var organizationDetail = await platformClient.GetAsync($"/api/admin/comercios/{owner.OrganizationId}");
        Assert.Equal("Suspendida", (await organizationDetail.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("estado").GetString());
        using var oldTokenRead = await ownerClient.GetAsync("/api/organizacion/actual");
        Assert.Equal(HttpStatusCode.Unauthorized, oldTokenRead.StatusCode);
        using var oldTokenCreate = await ownerClient.PostAsJsonAsync("/api/envios", new
        {
            OperadorId = Guid.NewGuid(), DestinatarioNombre = "Ana", DestinatarioEmail = "destinatario@example.test", Direccion = "Rivera 123", CodigoBulto = "SUSPENDED",
            PesoGramos = 100m, LargoCentimetros = 10m, AnchoCentimetros = 10m, AltoCentimetros = 10m
        });
        Assert.Equal(HttpStatusCode.Unauthorized, oldTokenCreate.StatusCode);

        using var suspendedLogin = await ownerClient.PostAsJsonAsync("/api/auth/login",
            new { Email = owner.Email, Password = AuthenticationTestData.Password });
        Assert.Equal(HttpStatusCode.OK, suspendedLogin.StatusCode);
        var suspendedSession = await suspendedLogin.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("OrganizacionSuspendida", suspendedSession.GetProperty("contexto").GetString());
        Assert.Equal("Comercio", suspendedSession.GetProperty("tipoOrganizacion").GetString());
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            suspendedSession.GetProperty("accessToken").GetString());
        using var limitedStatus = await ownerClient.GetAsync("/api/organizacion/suspendida");
        Assert.Equal(HttpStatusCode.OK, limitedStatus.StatusCode);
        Assert.Equal("Suspendida", (await limitedStatus.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("estado").GetString());
        using var deniedWork = await ownerClient.GetAsync("/api/envios");
        Assert.Equal(HttpStatusCode.Forbidden, deniedWork.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ownerClient.PatchAsJsonAsync($"/api/usuarios/{owner.InactiveEmployeeId}/email",
            new { Email = "suspended-edit@example.test", Version = 0 })).StatusCode);
        using var deniedCurrentContext = await ownerClient.GetAsync("/api/usuarios/actual");
        Assert.Equal(HttpStatusCode.Forbidden, deniedCurrentContext.StatusCode);

        using var idempotentSuspend = await platformClient.PutAsJsonAsync($"/api/admin/comercios/{owner.OrganizationId}/estado",
            new { Estado = "Suspendida", Motivo = "No debe sustituir el motivo ni crear un evento", Version = 0L });
        Assert.Equal(HttpStatusCode.OK, idempotentSuspend.StatusCode);
        await using (var db = database.CreateDbContext())
        {
            Assert.Single(await db.CambiosEstadoOrganizacion.ToListAsync());
            Assert.Equal(1, (await db.AccesosUsuario.SingleAsync(x => x.UsuarioId == owner.UserId)).VersionSesion);
            Assert.True((await db.AccesosUsuario.SingleAsync(x => x.UsuarioId == owner.UserId)).Activo);
            Assert.False((await db.AccesosUsuario.SingleAsync(x => x.UsuarioId == owner.InactiveEmployeeId)).Activo);
        }

        using var reactivated = await platformClient.PutAsJsonAsync($"/api/admin/comercios/{owner.OrganizationId}/estado",
            new { Estado = "Activa", Motivo = "Soporte completado", Version = 1L });
        Assert.Equal(HttpStatusCode.OK, reactivated.StatusCode);
        using var expiredRestrictedSession = await ownerClient.GetAsync("/api/organizacion/suspendida");
        Assert.Equal(HttpStatusCode.Unauthorized, expiredRestrictedSession.StatusCode);
        using var expiredOriginalSession = await SendWithBearerAsync(factory, "/api/organizacion/actual", owner.OriginalToken);
        Assert.Equal(HttpStatusCode.Unauthorized, expiredOriginalSession.StatusCode);

        var normalSession = await ownerClient.PostAsJsonAsync("/api/auth/login", new
        {
            Email = owner.Email,
            Password = AuthenticationTestData.Password
        });
        Assert.Equal(HttpStatusCode.OK, normalSession.StatusCode);
        var normalJson = await normalSession.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Comercio", normalJson.GetProperty("contexto").GetString());
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", normalJson.GetProperty("accessToken").GetString());
        using var current = await ownerClient.GetAsync("/api/organizacion/actual");
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal("Activa", (await current.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("estado").GetString());
        using var platformStillValid = await SendWithBearerAsync(factory, "/api/admin/comercios", platformToken!);
        Assert.Equal(HttpStatusCode.OK, platformStillValid.StatusCode);

        await using var verification = database.CreateDbContext();
        Assert.Equal(2, await verification.CambiosEstadoOrganizacion.CountAsync());
        Assert.Equal(2, (await verification.Comercios.SingleAsync(x => x.Id == owner.OrganizationId)).Version);
        var ownerAccess = await verification.AccesosUsuario.SingleAsync(x => x.UsuarioId == owner.UserId);
        Assert.True(ownerAccess.Activo);
        Assert.Equal(1, ownerAccess.VersionSesion);
        Assert.False((await verification.AccesosUsuario.SingleAsync(x => x.UsuarioId == owner.InactiveEmployeeId)).Activo);
    }

    private static async Task<OwnedOrganization> CreateOwnerAsync(IntegrationDatabase database, HttpClient client)
    {
        var email = $"owner-{Guid.NewGuid():N}@example.test";
        using var registered = await client.PostAsJsonAsync("/api/auth/register", new { Email = email, Password = AuthenticationTestData.Password });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        await LoginAsync(client, email);
        var identity = await client.GetFromJsonAsync<JsonElement>("/api/usuarios/actual");
        var userId = identity.GetProperty("usuarioId").GetGuid();
        using var created = await client.PostAsJsonAsync("/api/alta-propia/comercios", new { Nombre = "Owned commerce" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var organizationId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("organizacion").GetProperty("id").GetGuid();
        await LoginAsync(client, email);
        var originalToken = client.DefaultRequestHeaders.Authorization!.Parameter!;
        var employeeEmail = $"inactive-{Guid.NewGuid():N}@example.test";
        var employee = new Usuario(employeeEmail, AuthenticationTestData.Hash(employeeEmail));
        await using (var db = database.CreateDbContext())
        {
            var ownerRole = await db.Roles.SingleAsync(x => x.ComercioId == organizationId);
            var inactive = new AccesoUsuario(employee.Id, null, organizationId, ownerRole);
            inactive.CambiarEstado(false);
            db.AddRange(employee, inactive);
            await db.SaveChangesAsync();
        }
        return new(organizationId, userId, email, originalToken, employee.Id);
    }

    private static async Task InitializePlatformAsync(IntegrationDatabase database)
    {
        await using var db = database.CreateDbContext();
        await AuthenticationTestData.PlatformRepository(db).InicializarAsync(
            "platform-suspension@example.test", AuthenticationTestData.Password);
    }

    private static async Task LoginAsync(HttpClient client, string email)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { Email = email, Password = AuthenticationTestData.Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static async Task<HttpResponseMessage> SendWithBearerAsync(IntegrationApiFactory factory, string path, string token)
    {
        using var client = factory.CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private sealed record OwnedOrganization(Guid OrganizationId, Guid UserId, string Email, string OriginalToken, Guid InactiveEmployeeId);
}
