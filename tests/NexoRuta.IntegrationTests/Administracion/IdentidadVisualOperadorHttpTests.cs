using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Domain.Administracion;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;

namespace NexoRuta.IntegrationTests.Administracion;

public sealed class IdentidadVisualOperadorHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task EmployeeCanManageOwnBrand_CommerceWithSamePermissionCannotAndOperatorsStayIsolated()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var bootstrapApi = database.CreateApiFactory();
        using var seedClient = bootstrapApi.CreateHttpClient();
        var actors = await SeedActorsAsync(database);
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(database);
        using var api = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var operatorA = api.CreateHttpClient();
        using var operatorB = api.CreateHttpClient();
        using var commerce = api.CreateHttpClient();
        await operatorA.SetBearerForAccessAsync(database, actors.OperatorEmployeeA.AccessId);
        await operatorB.SetBearerForAccessAsync(database, actors.OperatorB.AccessId);
        await commerce.SetBearerForAccessAsync(database, actors.Commerce.AccessId);

        Assert.False(actors.OperatorEmployeeA.IsOwner);
        using var initial = await operatorA.GetAsync("/api/configuracion/identidad-visual");
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        var fallback = await initial.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, fallback.GetProperty("version").GetInt32());
        Assert.Equal("Integration operator", fallback.GetProperty("nombreMostrado").GetString());

        using var saved = await operatorA.PutAsync("/api/configuracion/identidad-visual",
            Form(0, "  Marca A  ", "contacto@marca-a.test", "+598 99 123 456", "#AABBCC", "#223344"));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var savedBrand = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, savedBrand.GetProperty("version").GetInt32());
        Assert.Equal("Marca A", savedBrand.GetProperty("nombreComercial").GetString());

        using var historyResponse = await operatorA.GetAsync("/api/configuracion/identidad-visual/versiones");
        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        var (history, _) = await ReadHistoryAsync(historyResponse);
        var firstVersion = Assert.Single(history!);
        Assert.Equal(1, firstVersion.GetProperty("version").GetInt32());
        Assert.Equal(actors.OperatorEmployeeA.UserId, firstVersion.GetProperty("autorUsuarioId").GetGuid());
        Assert.Equal("Marca A", firstVersion.GetProperty("nombreComercial").GetString());
        Assert.True(firstVersion.GetProperty("creadoEnUtc").GetDateTimeOffset() <= DateTimeOffset.UtcNow);

        using var noOp = await operatorA.PutAsync("/api/configuracion/identidad-visual",
            Form(1, "Marca A", "contacto@marca-a.test", "+598 99 123 456", "#AABBCC", "#223344"));
        Assert.Equal(HttpStatusCode.OK, noOp.StatusCode);
        using var historyAfterNoOp = await operatorA.GetAsync("/api/configuracion/identidad-visual/versiones");
        Assert.Single((await ReadHistoryAsync(historyAfterNoOp)).Versions);

        using var otherRead = await operatorB.GetAsync("/api/configuracion/identidad-visual");
        var otherBrand = await otherRead.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, otherRead.StatusCode);
        Assert.Equal(0, otherBrand.GetProperty("version").GetInt32());
        Assert.Equal("Second operator", otherBrand.GetProperty("nombreMostrado").GetString());
        using var otherHistory = await operatorB.GetAsync("/api/configuracion/identidad-visual/versiones");
        Assert.Empty((await ReadHistoryAsync(otherHistory)).Versions);

        using var publicBrandResponse = await commerce.GetAsync($"/api/operadores/{actors.OperatorAId}/identidad-visual");
        Assert.Equal(HttpStatusCode.OK, publicBrandResponse.StatusCode);
        var publicBrand = await publicBrandResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Marca A", publicBrand.GetProperty("nombre").GetString());
        Assert.False(publicBrand.TryGetProperty("autorUsuarioId", out _));
        Assert.False(publicBrand.TryGetProperty("correoAdministrativo", out _));

        for (var version = 2; version <= 12; version++)
        {
            using var next = await operatorA.PutAsync("/api/configuracion/identidad-visual",
                Form(version - 1, $"Marca A {version}"));
            Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        }
        using var recentHistoryResponse = await operatorA.GetAsync("/api/configuracion/identidad-visual/versiones");
        var recentPage = await ReadHistoryAsync(recentHistoryResponse);
        Assert.Equal(Enumerable.Range(3, 10).Reverse(), recentPage.Versions.Select(item => item.GetProperty("version").GetInt32()));
        Assert.Equal(3, recentPage.Cursor);
        using var olderHistoryResponse = await operatorA.GetAsync(
            $"/api/configuracion/identidad-visual/versiones?limite=10&antesDeVersion={recentPage.Cursor}");
        var olderPage = await ReadHistoryAsync(olderHistoryResponse);
        Assert.Equal(new[] { 2, 1 }, olderPage.Versions.Select(item => item.GetProperty("version").GetInt32()));
        Assert.Null(olderPage.Cursor);

        using var commerceRead = await commerce.GetAsync("/api/configuracion/identidad-visual");
        using var commerceHistory = await commerce.GetAsync("/api/configuracion/identidad-visual/versiones");
        using var commerceWrite = await commerce.PutAsync("/api/configuracion/identidad-visual", Form(0, "No permitido"));
        Assert.Equal(HttpStatusCode.Forbidden, commerceRead.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, commerceHistory.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, commerceWrite.StatusCode);
    }

    [Fact]
    public async Task ConcurrentSavesFromTwoAuthorizedUsersProduceOneAtomicVersionAndOneConflict()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var bootstrapApi = database.CreateApiFactory();
        using var seedClient = bootstrapApi.CreateHttpClient();
        var actors = await SeedActorsAsync(database);
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(database);
        using var api = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var first = api.CreateHttpClient();
        using var second = api.CreateHttpClient();
        await first.SetBearerForAccessAsync(database, actors.OperatorEmployeeA.AccessId);
        await second.SetBearerForAccessAsync(database, actors.OperatorEmployeeB.AccessId);

        using var firstRequest = new HttpRequestMessage(HttpMethod.Put, "/api/configuracion/identidad-visual")
        { Content = Form(0, "Writer A") };
        using var secondRequest = new HttpRequestMessage(HttpMethod.Put, "/api/configuracion/identidad-visual")
        { Content = Form(0, "Writer B") };
        var responses = await Task.WhenAll(first.SendAsync(firstRequest), second.SendAsync(secondRequest));
        using var firstResponse = responses[0];
        using var secondResponse = responses[1];
        var winners = new[] { firstResponse, secondResponse }.Where(response => response.StatusCode == HttpStatusCode.OK).ToArray();
        var conflicts = new[] { firstResponse, secondResponse }.Where(response => response.StatusCode == HttpStatusCode.Conflict).ToArray();
        Assert.Single(winners);
        Assert.Single(conflicts);

        var winnerJson = await winners[0].Content.ReadFromJsonAsync<JsonElement>();
        var winnerName = winnerJson.GetProperty("nombreComercial").GetString();
        var winnerActor = winnerName == "Writer A" ? actors.OperatorEmployeeA : actors.OperatorEmployeeB;
        using var historyResponse = await first.GetAsync("/api/configuracion/identidad-visual/versiones");
        var (history, _) = await ReadHistoryAsync(historyResponse);
        var version = Assert.Single(history);
        Assert.Equal(1, version.GetProperty("version").GetInt32());
        Assert.Equal(winnerName, version.GetProperty("nombreComercial").GetString());
        Assert.Equal(winnerActor.UserId, version.GetProperty("autorUsuarioId").GetGuid());
    }

    [Fact]
    public async Task LogoContentIsValidatedAndUploadReplacementAndRemovalAreVersioned()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var bootstrapApi = database.CreateApiFactory();
        using var seedClient = bootstrapApi.CreateHttpClient();
        var actors = await SeedActorsAsync(database);
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(database);
        using var api = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var operatorClient = api.CreateHttpClient();
        await operatorClient.SetBearerForAccessAsync(database, actors.OperatorEmployeeA.AccessId);
        var firstPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==");
        var replacementPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYPj/HwADAgH/5ncLrgAAAABJRU5ErkJggg==");

        using var invalid = await operatorClient.PutAsync("/api/configuracion/identidad-visual",
            Form(0, logo: [1, 2, 3, 4], logoName: "spoofed.png"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var invalidChecksum = await operatorClient.PutAsync("/api/configuracion/identidad-visual",
            Form(0, logo: Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/S3sAAAAASUVORK5CYII=")));
        Assert.Equal(HttpStatusCode.BadRequest, invalidChecksum.StatusCode);
        using var emptyHistory = await operatorClient.GetAsync("/api/configuracion/identidad-visual/versiones");
        Assert.Empty((await ReadHistoryAsync(emptyHistory)).Versions);

        using var uploaded = await operatorClient.PutAsync("/api/configuracion/identidad-visual",
            Form(0, "Marca con logo", logo: firstPng, logoName: "marca.png"));
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        var first = await uploaded.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, first.GetProperty("version").GetInt32());
        Assert.Equal(Convert.ToBase64String(firstPng), first.GetProperty("logoContenido").GetString());
        Assert.Equal("image/png", first.GetProperty("logoTipoContenido").GetString());

        using var replaced = await operatorClient.PutAsync("/api/configuracion/identidad-visual",
            Form(1, "Marca con logo", logo: replacementPng, logoName: "reemplazo.png"));
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        var second = await replaced.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, second.GetProperty("version").GetInt32());
        Assert.Equal(Convert.ToBase64String(replacementPng), second.GetProperty("logoContenido").GetString());

        using var removed = await operatorClient.PutAsync("/api/configuracion/identidad-visual",
            Form(2, "Marca con logo", quitarLogo: true));
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        var third = await removed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, third.GetProperty("version").GetInt32());
        Assert.Equal(JsonValueKind.Null, third.GetProperty("logoContenido").ValueKind);

        using var historyResponse = await operatorClient.GetAsync("/api/configuracion/identidad-visual/versiones");
        var (history, _) = await ReadHistoryAsync(historyResponse);
        Assert.Equal(new[] { 3, 2, 1 }, history.Select(version => version.GetProperty("version").GetInt32()));
        Assert.Equal(Convert.ToBase64String(replacementPng), history[1].GetProperty("logoContenido").GetString());
        Assert.Equal(Convert.ToBase64String(firstPng), history[2].GetProperty("logoContenido").GetString());
    }

    private static async Task<Actors> SeedActorsAsync(IntegrationDatabase database)
    {
        await using var db = database.CreateDbContext();
        var operatorSeed = await db.AccesosUsuario.SingleAsync(access => access.OperadorId != null);
        var commerceSeed = await db.AccesosUsuario.SingleAsync(access => access.ComercioId != null);
        var permission = await db.Permisos.SingleAsync(item => item.Codigo == "configuracion.gestionar");
        var operatorRole = await db.Roles.SingleAsync(role => role.Id == operatorSeed.RolId);
        var commerceRole = await db.Roles.SingleAsync(role => role.Id == commerceSeed.RolId);

        var employeeA = Employee("employee-a");
        var employeeB = Employee("employee-b");
        var employeeAAccess = new AccesoUsuario(employeeA.Id, operatorSeed.OperadorId, null, operatorRole);
        var employeeBAccess = new AccesoUsuario(employeeB.Id, operatorSeed.OperadorId, null, operatorRole);
        var secondOperator = new Operador("Second operator");
        db.Operadores.Add(secondOperator);
        await db.SaveChangesAsync();
        var secondOperatorRole = new Rol("administrator", "Second operator administrator", secondOperator.Id, null);
        db.Roles.Add(secondOperatorRole);
        await db.SaveChangesAsync();
        var secondOperatorEmployee = Employee("operator-b");
        var secondOperatorAccess = new AccesoUsuario(secondOperatorEmployee.Id, secondOperator.Id, null, secondOperatorRole);
        var commerceEmployee = Employee("commerce-user");
        var commerceEmployeeAccess = new AccesoUsuario(commerceEmployee.Id, null, commerceSeed.ComercioId, commerceRole);

        db.AddRange(employeeA, employeeB, employeeAAccess, employeeBAccess,
            secondOperatorEmployee, secondOperatorAccess,
            commerceEmployee, commerceEmployeeAccess);
        if (!await db.RolesPermisos.AnyAsync(grant => grant.RolId == commerceRole.Id && grant.PermisoId == permission.Id))
            db.RolesPermisos.Add(new RolPermiso(commerceRole.Id, permission.Id));
        db.RolesPermisos.Add(new RolPermiso(secondOperatorRole.Id, permission.Id));
        await db.SaveChangesAsync();
        return new(operatorSeed.OperadorId!.Value, employeeA.Id, employeeAAccess.Id,
            employeeB.Id, employeeBAccess.Id, secondOperator.Id, secondOperatorEmployee.Id,
            secondOperatorAccess.Id, commerceEmployee.Id, commerceEmployeeAccess.Id);

        static Usuario Employee(string prefix)
        {
            var email = $"{prefix}-{Guid.NewGuid():N}@example.test";
            return new Usuario(email, AuthenticationTestData.Hash(email));
        }
    }

    private static async Task<(JsonElement[] Versions, int? Cursor)> ReadHistoryAsync(HttpResponseMessage response)
    {
        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (page.GetProperty("versiones").EnumerateArray().ToArray(),
            page.GetProperty("cursorAnterior").ValueKind == JsonValueKind.Null
                ? null : page.GetProperty("cursorAnterior").GetInt32());
    }

    private static MultipartFormDataContent Form(int version, string? name = null, string? email = null,
        string? phone = null, string? primary = null, string? secondary = null, bool quitarLogo = false,
        byte[]? logo = null, string logoName = "logo.png")
    {
        var form = new MultipartFormDataContent();
        Add("VersionEsperada", version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add("NombreComercial", name ?? "");
        Add("CorreoPublico", email ?? "");
        Add("TelefonoPublico", phone ?? "");
        Add("ColorPrimario", primary ?? "");
        Add("ColorSecundario", secondary ?? "");
        Add("QuitarLogo", quitarLogo ? "true" : "false");
        if (logo is not null)
        {
            var file = new ByteArrayContent(logo);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "Logo", logoName);
        }
        return form;

        void Add(string key, string value) => form.Add(new StringContent(value), key);
    }

    private sealed record Actor(Guid UserId, Guid AccessId, bool IsOwner);
    private sealed record Actors(Guid OperatorAId, Guid EmployeeAUserId, Guid EmployeeAAccessId,
        Guid EmployeeBUserId, Guid EmployeeBAccessId, Guid OperatorBId, Guid OperatorBUserId,
        Guid OperatorBAccessId, Guid CommerceUserId, Guid CommerceAccessId)
    {
        public Actor OperatorEmployeeA => new(EmployeeAUserId, EmployeeAAccessId, false);
        public Actor OperatorEmployeeB => new(EmployeeBUserId, EmployeeBAccessId, false);
        public Actor OperatorB => new(OperatorBUserId, OperatorBAccessId, true);
        public Actor Commerce => new(CommerceUserId, CommerceAccessId, false);
    }
}
