using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Domain.Administracion;
using NexoRuta.IntegrationTests.Fixtures;

namespace NexoRuta.IntegrationTests.Administracion;

public sealed class SolicitudesBajaHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task OwnerCanRequestAndPlatformCanApproveOrRejectWithoutDeletingOrganization()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = Factory(database);
        using var ownerClient = factory.CreateHttpClient();
        var owner = await CreateOwnerAsync(database, ownerClient);
        await InitializePlatformAsync(database);
        using var platformClient = factory.CreateHttpClient();
        await LoginAsync(platformClient, "platform-lifecycle@example.test");

        using var request = await ownerClient.PostAsJsonAsync("/api/organizacion/actual/solicitudes-baja",
            new { Motivo = "Cerramos el comercio al terminar el mes" });
        Assert.Equal(HttpStatusCode.Created, request.StatusCode);
        var submitted = await request.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Pendiente", submitted.GetProperty("estado").GetString());

        using var ownState = await ownerClient.GetAsync("/api/organizacion/actual");
        Assert.Equal(HttpStatusCode.OK, ownState.StatusCode);
        Assert.Equal("Activa", (await ownState.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("estado").GetString());

        using var pendingList = await platformClient.GetAsync("/api/admin/solicitudes-baja?estado=Pendiente");
        Assert.Equal(HttpStatusCode.OK, pendingList.StatusCode);
        var page = await pendingList.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, page.GetProperty("total").GetInt32());
        var requestId = submitted.GetProperty("id").GetGuid();

        using var rejected = await platformClient.PostAsJsonAsync($"/api/admin/solicitudes-baja/{requestId}/resolver",
            new { Decision = "Rechazar", Motivo = "No corresponde la baja solicitada" });
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        var rejectedRequest = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        using var incompatibleResolution = await platformClient.PostAsJsonAsync($"/api/admin/solicitudes-baja/{requestId}/resolver",
            new { Decision = "Aprobar", Motivo = "No se puede cambiar una resoluciÃ³n", Version = rejectedRequest.GetProperty("version").GetInt64() });
        Assert.Equal(HttpStatusCode.Conflict, incompatibleResolution.StatusCode);
        await using (var db = database.CreateDbContext())
        {
            Assert.Equal(EstadoOrganizacion.Activa, (await db.Comercios.SingleAsync(x => x.Id == owner.OrganizationId)).Estado);
            Assert.Equal(EstadoSolicitudBaja.Rechazada, (await db.SolicitudesBajaOrganizacion.SingleAsync(x => x.Id == requestId)).Estado);
            Assert.Empty(await db.CambiosEstadoOrganizacion.ToListAsync());
        }

        using var secondRequest = await ownerClient.PostAsJsonAsync("/api/organizacion/actual/solicitudes-baja",
            new { Motivo = "Confirmamos el cierre" });
        Assert.Equal(HttpStatusCode.Created, secondRequest.StatusCode);
        var secondId = (await secondRequest.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var approved = await platformClient.PostAsJsonAsync($"/api/admin/solicitudes-baja/{secondId}/resolver",
            new { Decision = "Aprobar", Motivo = "Solicitud validada" });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        using var revokedOwnerToken = await ownerClient.GetAsync("/api/organizacion/actual");
        Assert.Equal(HttpStatusCode.Unauthorized, revokedOwnerToken.StatusCode);

        await using var verification = database.CreateDbContext();
        Assert.Equal(EstadoOrganizacion.Suspendida, (await verification.Comercios.SingleAsync(x => x.Id == owner.OrganizationId)).Estado);
        Assert.Equal(EstadoSolicitudBaja.Aprobada, (await verification.SolicitudesBajaOrganizacion.SingleAsync(x => x.Id == secondId)).Estado);
        var change = await verification.CambiosEstadoOrganizacion.SingleAsync();
        Assert.Equal(owner.OrganizationId, change.ComercioId);
        Assert.Equal("Solicitud validada", change.Motivo);
        Assert.True(await verification.Comercios.AnyAsync(x => x.Id == owner.OrganizationId));
        Assert.True(await verification.AccesosUsuario.AnyAsync(x => x.UsuarioId == owner.UserId && x.Activo));
    }

    [Fact]
    public async Task OnlyPersistedOwnerCanSubmitAndConcurrentRequestsLeaveOnePending()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = Factory(database);
        using var ownerClient = factory.CreateHttpClient();
        var owner = await CreateOwnerAsync(database, ownerClient);

        var employeeEmail = $"employee-{Guid.NewGuid():N}@example.test";
        await using (var db = database.CreateDbContext())
        {
            var role = new Rol("employee", "Employee", null, owner.OrganizationId);
            var employee = new Usuario(employeeEmail, AuthenticationTestData.Hash(employeeEmail));
            db.AddRange(role, employee, new AccesoUsuario(employee.Id, null, owner.OrganizationId, role));
            await db.SaveChangesAsync();
        }
        using var employeeClient = factory.CreateHttpClient();
        await LoginAsync(employeeClient, employeeEmail);
        using var employeeHistory = await employeeClient.GetAsync("/api/organizacion/actual/solicitudes-baja");
        Assert.Equal(HttpStatusCode.OK, employeeHistory.StatusCode);
        using var employeeRequest = await employeeClient.PostAsJsonAsync("/api/organizacion/actual/solicitudes-baja",
            new { Motivo = "Employee cannot request" });
        Assert.Equal(HttpStatusCode.Forbidden, employeeRequest.StatusCode);

        await InitializePlatformAsync(database);
        using var platformClient = factory.CreateHttpClient();
        await LoginAsync(platformClient, "platform-lifecycle@example.test");
        using var platformRequest = await platformClient.PostAsJsonAsync("/api/organizacion/actual/solicitudes-baja",
            new { Motivo = "Platform is not an organization owner" });
        Assert.Equal(HttpStatusCode.Forbidden, platformRequest.StatusCode);

        var responses = await Task.WhenAll(
            ownerClient.PostAsJsonAsync("/api/organizacion/actual/solicitudes-baja", new { Motivo = "Concurrent one" }),
            ownerClient.PostAsJsonAsync("/api/organizacion/actual/solicitudes-baja", new { Motivo = "Concurrent two" }));
        Assert.Equal(new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }, responses.Select(x => x.StatusCode).Order());
        foreach (var response in responses) response.Dispose();
        await using var verification = database.CreateDbContext();
        Assert.Single(await verification.SolicitudesBajaOrganizacion.Where(x => x.Estado == EstadoSolicitudBaja.Pendiente).ToListAsync());
        Assert.Equal(EstadoOrganizacion.Activa, (await verification.Comercios.SingleAsync(x => x.Id == owner.OrganizationId)).Estado);
    }

    [Fact]
    public async Task ConcurrentIncompatibleResolutionsCommitOnlyOneDecisionAndItsEffects()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = Factory(database);
        using var ownerClient = factory.CreateHttpClient();
        var owner = await CreateOwnerAsync(database, ownerClient);
        await InitializePlatformAsync(database);
        using var platform = factory.CreateHttpClient();
        await LoginAsync(platform, "platform-lifecycle@example.test");
        using var created = await ownerClient.PostAsJsonAsync("/api/organizacion/actual/solicitudes-baja",
            new { Motivo = "Cierre solicitado" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var requestId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var outcomes = await Task.WhenAll(
            platform.PostAsJsonAsync($"/api/admin/solicitudes-baja/{requestId}/resolver",
                new { Decision = "Aprobar", Motivo = "Aprobación simultánea", Version = 0L }),
            platform.PostAsJsonAsync($"/api/admin/solicitudes-baja/{requestId}/resolver",
                new { Decision = "Rechazar", Motivo = "Rechazo simultáneo", Version = 0L }));
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, outcomes.Select(x => x.StatusCode).Order());
        var approved = outcomes.Single(x => x.StatusCode == HttpStatusCode.OK);
        var resolved = await approved.Content.ReadFromJsonAsync<JsonElement>();
        var state = resolved.GetProperty("estado").GetString();
        await using var verify = database.CreateDbContext();
        Assert.Equal(state, (await verify.SolicitudesBajaOrganizacion.SingleAsync(x => x.Id == requestId)).Estado.ToString());
        Assert.Equal(state == "Aprobada" ? EstadoOrganizacion.Suspendida : EstadoOrganizacion.Activa,
            (await verify.Comercios.SingleAsync(x => x.Id == owner.OrganizationId)).Estado);
        Assert.Equal(state == "Aprobada" ? 1 : 0, await verify.CambiosEstadoOrganizacion.CountAsync());
        foreach (var outcome in outcomes) outcome.Dispose();
    }

    private static IntegrationApiFactory Factory(IntegrationDatabase database)
        => new(database, settings: new Dictionary<string, string?> { ["DatabaseInitialization:SeedData"] = "false" });

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
        await using var db = database.CreateDbContext();
        var access = await db.AccesosUsuario.SingleAsync(x => x.UsuarioId == userId);
        return new(organizationId, userId, access.Id);
    }

    private static async Task InitializePlatformAsync(IntegrationDatabase database)
    {
        const string email = "platform-lifecycle@example.test";
        await using (var db = database.CreateDbContext())
            await AuthenticationTestData.PlatformRepository(db).InicializarAsync(email, AuthenticationTestData.Password);
    }

    private static async Task LoginAsync(HttpClient client, string email)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { Email = email, Password = AuthenticationTestData.Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private sealed record OwnedOrganization(Guid OrganizationId, Guid UserId, Guid AccessId);
}
