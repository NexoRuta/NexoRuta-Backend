using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Domain.Administracion;
using NexoRuta.IntegrationTests.Fixtures;

namespace NexoRuta.IntegrationTests.Administracion;

public sealed class SolicitudesReactivacionHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task ConcurrentIncompatibleResolutionsCommitOneDecisionAndMatchingState()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = new IntegrationApiFactory(database, settings: new Dictionary<string, string?>
        { ["DatabaseInitialization:SeedData"] = "false" });
        using var owner = factory.CreateHttpClient();
        var email = $"race-{Guid.NewGuid():N}@example.test";
        using var register = await owner.PostAsJsonAsync("/api/auth/register", new { Email = email, Password = AuthenticationTestData.Password });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        await LoginAsync(owner, email);
        using var created = await owner.PostAsJsonAsync("/api/alta-propia/operadores", new { Nombre = "Race operator" });
        var organizationId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("organizacion").GetProperty("id").GetGuid();
        await using (var db = database.CreateDbContext())
            await AuthenticationTestData.PlatformRepository(db).InicializarAsync("platform-race@example.test", AuthenticationTestData.Password);
        using var admin = factory.CreateHttpClient();
        await LoginAsync(admin, "platform-race@example.test");
        using var suspended = await admin.PutAsJsonAsync($"/api/admin/operadores/{organizationId}/estado",
            new { Estado = "Suspendida", Motivo = "Support", Version = 0L });
        Assert.Equal(HttpStatusCode.OK, suspended.StatusCode);
        await LoginAsync(owner, email, "OrganizacionSuspendida");
        using var requested = await owner.PostAsJsonAsync("/api/organizacion/suspendida/solicitudes-reactivacion",
            new { Motivo = "Necesidad operativa" });
        Assert.Equal(HttpStatusCode.Created, requested.StatusCode);
        var requestId = (await requested.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var responses = await Task.WhenAll(
            admin.PostAsJsonAsync($"/api/admin/solicitudes-reactivacion/{requestId}/resolver",
                new { Decision = "Aprobar", Motivo = "Aprobación concurrente", Version = 0L }),
            admin.PostAsJsonAsync($"/api/admin/solicitudes-reactivacion/{requestId}/resolver",
                new { Decision = "Rechazar", Motivo = "Rechazo concurrente", Version = 0L }));
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, responses.Select(x => x.StatusCode).Order());
        var winner = await responses.Single(x => x.StatusCode == HttpStatusCode.OK).Content.ReadFromJsonAsync<JsonElement>();
        await using var verify = database.CreateDbContext();
        Assert.Equal(winner.GetProperty("estado").GetString(),
            (await verify.SolicitudesReactivacionOrganizacion.SingleAsync()).Estado.ToString());
        Assert.Equal(winner.GetProperty("estado").GetString() == "Aprobada" ? EstadoOrganizacion.Activa : EstadoOrganizacion.Suspendida,
            (await verify.Operadores.SingleAsync(x => x.Id == organizationId)).Estado);
        Assert.Equal(winner.GetProperty("estado").GetString() == "Aprobada" ? 2 : 1,
            await verify.CambiosEstadoOrganizacion.CountAsync());
        foreach (var response in responses) response.Dispose();
    }

    [Fact]
    public async Task ConcurrentRequestsStayUniqueAndDirectSupportReactivationClosesPendingRequestOnce()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = new IntegrationApiFactory(database, settings: new Dictionary<string, string?>
        { ["DatabaseInitialization:SeedData"] = "false" });
        using var owner = factory.CreateHttpClient();
        var email = $"support-{Guid.NewGuid():N}@example.test";
        using var registered = await owner.PostAsJsonAsync("/api/auth/register", new { Email = email, Password = AuthenticationTestData.Password });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        await LoginAsync(owner, email);
        using var created = await owner.PostAsJsonAsync("/api/alta-propia/comercios", new { Nombre = "Support commerce" });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("organizacion").GetProperty("id").GetGuid();
        await using (var db = database.CreateDbContext())
            await AuthenticationTestData.PlatformRepository(db).InicializarAsync("platform-support@example.test", AuthenticationTestData.Password);
        using var admin = factory.CreateHttpClient();
        await LoginAsync(admin, "platform-support@example.test");
        using var suspended = await admin.PutAsJsonAsync($"/api/admin/comercios/{id}/estado",
            new { Estado = "Suspendida", Motivo = "Support", Version = 0L });
        Assert.Equal(HttpStatusCode.OK, suspended.StatusCode);
        await LoginAsync(owner, email, "OrganizacionSuspendida");
        var responses = await Task.WhenAll(
            owner.PostAsJsonAsync("/api/organizacion/suspendida/solicitudes-reactivacion", new { Motivo = "Primera" }),
            owner.PostAsJsonAsync("/api/organizacion/suspendida/solicitudes-reactivacion", new { Motivo = "Segunda" }));
        Assert.Equal(new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }, responses.Select(x => x.StatusCode).Order());
        foreach (var response in responses) response.Dispose();

        using var activated = await admin.PutAsJsonAsync($"/api/admin/comercios/{id}/estado",
            new { Estado = "Activa", Motivo = "Soporte reactivó directamente", Version = 1L });
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        using var repeated = await admin.PutAsJsonAsync($"/api/admin/comercios/{id}/estado",
            new { Estado = "Activa", Motivo = "Repetición", Version = 1L });
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        using var oldRestricted = await owner.GetAsync("/api/organizacion/suspendida");
        Assert.Equal(HttpStatusCode.Unauthorized, oldRestricted.StatusCode);
        await using var verify = database.CreateDbContext();
        Assert.Single(await verify.SolicitudesReactivacionOrganizacion.ToListAsync());
        Assert.Equal(EstadoSolicitudReactivacion.AtendidaPorSoporte,
            (await verify.SolicitudesReactivacionOrganizacion.SingleAsync()).Estado);
        Assert.Equal(2, await verify.CambiosEstadoOrganizacion.CountAsync());
        Assert.Equal(EstadoOrganizacion.Activa, (await verify.Comercios.SingleAsync(x => x.Id == id)).Estado);
    }

    [Fact]
    public async Task ActiveEmployeeSeesNoticeButCannotRequestAndDisabledEmployeeCannotLogin()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = new IntegrationApiFactory(database, settings: new Dictionary<string, string?>
        { ["DatabaseInitialization:SeedData"] = "false" });
        using var owner = factory.CreateHttpClient();
        var ownerEmail = $"owner-{Guid.NewGuid():N}@example.test";
        using var registered = await owner.PostAsJsonAsync("/api/auth/register", new { Email = ownerEmail, Password = AuthenticationTestData.Password });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        await LoginAsync(owner, ownerEmail);
        using var created = await owner.PostAsJsonAsync("/api/alta-propia/comercios", new { Nombre = "Protected commerce" });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("organizacion").GetProperty("id").GetGuid();
        var employeeEmail = $"employee-{Guid.NewGuid():N}@example.test";
        var disabledEmail = $"disabled-{Guid.NewGuid():N}@example.test";
        await using (var db = database.CreateDbContext())
        {
            var role = await db.Roles.SingleAsync(x => x.ComercioId == id);
            var employee = new Usuario(employeeEmail, AuthenticationTestData.Hash(employeeEmail));
            var disabled = new Usuario(disabledEmail, AuthenticationTestData.Hash(disabledEmail));
            var disabledAccess = new AccesoUsuario(disabled.Id, null, id, role);
            disabledAccess.CambiarEstado(false);
            db.AddRange(employee, new AccesoUsuario(employee.Id, null, id, role), disabled, disabledAccess);
            await db.SaveChangesAsync();
            await AuthenticationTestData.PlatformRepository(db).InicializarAsync("platform-employee@example.test", AuthenticationTestData.Password);
        }
        using var admin = factory.CreateHttpClient();
        await LoginAsync(admin, "platform-employee@example.test");
        using var suspended = await admin.PutAsJsonAsync($"/api/admin/comercios/{id}/estado",
            new { Estado = "Suspendida", Motivo = "Support", Version = 0L });
        Assert.Equal(HttpStatusCode.OK, suspended.StatusCode);
        using var employeeClient = factory.CreateHttpClient();
        await LoginAsync(employeeClient, employeeEmail, "OrganizacionSuspendida");
        using var notice = await employeeClient.GetAsync("/api/organizacion/suspendida");
        Assert.False((await notice.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("esPropietario").GetBoolean());
        using var history = await employeeClient.GetAsync("/api/organizacion/suspendida/solicitudes-reactivacion");
        Assert.Equal(HttpStatusCode.Forbidden, history.StatusCode);
        using var attempt = await employeeClient.PostAsJsonAsync("/api/organizacion/suspendida/solicitudes-reactivacion",
            new { Motivo = "Employee cannot request" });
        Assert.Equal(HttpStatusCode.Forbidden, attempt.StatusCode);
        using var disabledClient = factory.CreateHttpClient();
        using var login = await disabledClient.PostAsJsonAsync("/api/auth/login",
            new { Email = disabledEmail, Password = AuthenticationTestData.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        await using var verify = database.CreateDbContext();
        Assert.Empty(await verify.SolicitudesReactivacionOrganizacion.ToListAsync());
    }

    [Theory]
    [InlineData("comercios", "Comercio")]
    [InlineData("operadores", "Operador")]
    public async Task OwnerCanRetryAfterRejectionAndApprovalRequiresFreshOperationalLogin(string route, string expectedContext)
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = new IntegrationApiFactory(database, settings: new Dictionary<string, string?>
        { ["DatabaseInitialization:SeedData"] = "false" });
        using var owner = factory.CreateHttpClient();
        var email = $"reactivation-{Guid.NewGuid():N}@example.test";
        using var register = await owner.PostAsJsonAsync("/api/auth/register", new { Email = email, Password = AuthenticationTestData.Password });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        var identityToken = await LoginAsync(owner, email);
        using var created = await owner.PostAsJsonAsync($"/api/alta-propia/{route}", new { Nombre = "Organization to resume" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("organizacion").GetProperty("id").GetGuid();
        var oldOperationalToken = await LoginAsync(owner, email);
        var inactiveEmail = $"inactive-{Guid.NewGuid():N}@example.test";
        await using (var db = database.CreateDbContext())
        {
            var role = await db.Roles.SingleAsync(x => route == "comercios" ? x.ComercioId == id : x.OperadorId == id);
            var inactiveUser = new Usuario(inactiveEmail, AuthenticationTestData.Hash(inactiveEmail));
            var inactiveAccess = new AccesoUsuario(inactiveUser.Id, route == "operadores" ? id : null,
                route == "comercios" ? id : null, role);
            inactiveAccess.CambiarEstado(false);
            db.AddRange(inactiveUser, inactiveAccess);
            await db.SaveChangesAsync();
        }

        await using (var db = database.CreateDbContext())
            await AuthenticationTestData.PlatformRepository(db).InicializarAsync("platform-reactivation@example.test", AuthenticationTestData.Password);
        using var admin = factory.CreateHttpClient();
        await LoginAsync(admin, "platform-reactivation@example.test");
        using var suspended = await admin.PutAsJsonAsync($"/api/admin/{route}/{id}/estado",
            new { Estado = "Suspendida", Motivo = "Soporte", Version = 0L });
        Assert.Equal(HttpStatusCode.OK, suspended.StatusCode);
        using var oldAccess = await owner.GetAsync("/api/organizacion/actual");
        Assert.Equal(HttpStatusCode.Unauthorized, oldAccess.StatusCode);

        var restrictedToken = await LoginAsync(owner, email, "OrganizacionSuspendida");
        using var status = await owner.GetAsync("/api/organizacion/suspendida");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.True((await status.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("esPropietario").GetBoolean());
        using var forbiddenWork = await owner.GetAsync("/api/organizacion/actual");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenWork.StatusCode);
        using var forbiddenAdmin = await owner.GetAsync("/api/admin/solicitudes-reactivacion");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenAdmin.StatusCode);
        using var invalidRequest = await owner.PostAsJsonAsync("/api/organizacion/suspendida/solicitudes-reactivacion",
            new { Motivo = " " });
        Assert.Equal(HttpStatusCode.BadRequest, invalidRequest.StatusCode);

        using var request = await owner.PostAsJsonAsync("/api/organizacion/suspendida/solicitudes-reactivacion",
            new { Motivo = "Necesitamos volver a operar", OrganizacionId = Guid.NewGuid(), SolicitadaPorUsuarioId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Created, request.StatusCode);
        var first = await request.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, first.GetProperty("organizacionId").GetGuid());
        using var duplicate = await owner.PostAsJsonAsync("/api/organizacion/suspendida/solicitudes-reactivacion", new { Motivo = "Duplicada" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        await using (var db = database.CreateDbContext())
            Assert.Equal(EstadoOrganizacion.Suspendida, route == "comercios"
                ? (await db.Comercios.SingleAsync(x => x.Id == id)).Estado
                : (await db.Operadores.SingleAsync(x => x.Id == id)).Estado);

        var firstId = first.GetProperty("id").GetGuid();
        using var invalidResolution = await admin.PostAsJsonAsync($"/api/admin/solicitudes-reactivacion/{firstId}/resolver",
            new { Decision = "Rechazar", Motivo = " ", Version = 0L });
        Assert.Equal(HttpStatusCode.BadRequest, invalidResolution.StatusCode);
        using var rejected = await admin.PostAsJsonAsync($"/api/admin/solicitudes-reactivacion/{firstId}/resolver",
            new { Decision = "Rechazar", Motivo = "Falta documentación", Version = 0L });
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        using var incompatible = await admin.PostAsJsonAsync($"/api/admin/solicitudes-reactivacion/{firstId}/resolver",
            new { Decision = "Aprobar", Motivo = "Cambio", Version = 0L });
        Assert.Equal(HttpStatusCode.Conflict, incompatible.StatusCode);
        using var history = await owner.GetAsync("/api/organizacion/suspendida/solicitudes-reactivacion");
        Assert.Contains("Falta documentación", await history.Content.ReadAsStringAsync());
        using var retry = await owner.PostAsJsonAsync("/api/organizacion/suspendida/solicitudes-reactivacion",
            new { Motivo = "Documentación completa" });
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        var secondId = (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var approved = await admin.PostAsJsonAsync($"/api/admin/solicitudes-reactivacion/{secondId}/resolver",
            new { Decision = "Aprobar", Motivo = "Verificado", Version = 0L });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        using var expiredRestricted = await owner.GetAsync("/api/organizacion/suspendida");
        Assert.Equal(HttpStatusCode.Unauthorized, expiredRestricted.StatusCode);
        using var oldTokenCheck = await factory.CreateHttpClient().SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/organizacion/actual")
        { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", oldOperationalToken) } });
        Assert.Equal(HttpStatusCode.Unauthorized, oldTokenCheck.StatusCode);
        await LoginAsync(owner, email, expectedContext);
        using var current = await owner.GetAsync("/api/organizacion/actual");
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        await using var verify = database.CreateDbContext();
        Assert.Equal(2, await verify.SolicitudesReactivacionOrganizacion.CountAsync());
        Assert.Equal(2, await verify.CambiosEstadoOrganizacion.CountAsync());
        Assert.True(await verify.AccesosUsuario.AnyAsync(x => x.UsuarioId == first.GetProperty("solicitadaPorUsuarioId").GetGuid() && x.EsPropietario));
        var inactiveUserId = await verify.Usuarios.Where(x => x.Email == inactiveEmail).Select(x => x.Id).SingleAsync();
        Assert.False((await verify.AccesosUsuario.SingleAsync(x => x.UsuarioId == inactiveUserId)).Activo);
    }

    private static async Task<string> LoginAsync(HttpClient client, string email, string? context = null)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { Email = email, Password = AuthenticationTestData.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (context is not null) Assert.Equal(context, json.GetProperty("contexto").GetString());
        var token = json.GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return token;
    }
}
