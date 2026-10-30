using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Administracion.Repositories;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.IntegrationTests.Fixtures;
using Npgsql;

namespace NexoRuta.IntegrationTests.ReglasOperativas;

public sealed class ReglasOperativasHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string BasePath = "/api/configuracion/reglas-operativas";

    [Fact]
    public async Task EmptyStateDraftPublicationHistoryAndForeignIdsUseThePublicContract()
    {
        var database = await postgres.CreateDatabaseAsync();
        string operatorEmail;
        using (var bootstrap = new IntegrationApiFactory(database, seedOperationalRules: false))
        using (bootstrap.CreateHttpClient())
        {
            await using var db = database.CreateDbContext();
            var access = await db.AccesosUsuario.Where(x => x.OperadorId != null).FirstAsync();
            operatorEmail = await db.Usuarios.Where(x => x.Id == access.UsuarioId)
                .Select(x => x.Email).SingleAsync();
        }

        var settings = Settings(await CreateRestrictedApiConnectionAsync(database));
        using var factory = new IntegrationApiFactory(database, environment: "Development", settings: settings,
            replaceDbContext: false);
        using var client = factory.CreateHttpClient();
        await LoginAsync(client, operatorEmail);

        using var initial = await client.GetAsync(BasePath);
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        using var initialJson = JsonDocument.Parse(await initial.Content.ReadAsStringAsync());
        Assert.False(initialJson.RootElement.GetProperty("configurado").GetBoolean());
        Assert.Equal(JsonValueKind.Null, initialJson.RootElement.GetProperty("borrador").ValueKind);
        Assert.Equal(JsonValueKind.Null, initialJson.RootElement.GetProperty("publicacionVigente").ValueKind);
        await using (var readOnlyCheck = database.CreateDbContext())
            Assert.Equal(0, await readOnlyCheck.ReglasOperativasOperadores.CountAsync());
        using var absentDraft = await client.GetAsync(BasePath + "/borrador");
        Assert.Equal(HttpStatusCode.NotFound, absentDraft.StatusCode);

        using var created = await client.PostAsync(BasePath + "/borrador", null);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Contains(BasePath + "/borrador", created.Headers.Location!.ToString(), StringComparison.Ordinal);
        var initialDraft = await created.Content.ReadFromJsonAsync<JsonElement>();
        var originalDraftId = initialDraft.GetProperty("versionReglasOperativasId").GetGuid();
        Assert.Equal(0, initialDraft.GetProperty("versionConcurrencia").GetInt64());
        Assert.Equal(JsonValueKind.Null, initialDraft.GetProperty("numeroVersionPublicada").ValueKind);
        Assert.Equal(JsonValueKind.Null, initialDraft.GetProperty("maxIntentosEntrega").ValueKind);
        Assert.Equal(JsonValueKind.Null, initialDraft.GetProperty("politicaDevolucionAlAgotarIntentos").ValueKind);
        Assert.Empty(initialDraft.GetProperty("requisitosPrueba").EnumerateArray());
        Assert.Empty(initialDraft.GetProperty("motivos").EnumerateArray());

        using var incompleteSave = await client.PutAsJsonAsync(BasePath + "/borrador", Configuration(0,
            maxAttempts: null, interval: null, policy: null, standard: null, urgent: null,
            requirements: [], motives: [], proofDeadline: null));
        Assert.Equal(HttpStatusCode.OK, incompleteSave.StatusCode);
        var incompleteDraft = await incompleteSave.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, incompleteDraft.GetProperty("versionConcurrencia").GetInt64());
        Assert.Equal(JsonValueKind.Null, incompleteDraft.GetProperty("maxIntentosEntrega").ValueKind);
        Assert.Equal(JsonValueKind.Null, incompleteDraft.GetProperty("plazoUrgenteMinutos").ValueKind);

        using var incompletePublish = await client.PostAsJsonAsync(BasePath + "/borrador/publicar",
            new { versionConcurrencia = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, incompletePublish.StatusCode);
        using var incompleteError = JsonDocument.Parse(await incompletePublish.Content.ReadAsStringAsync());
        var missingFields = incompleteError.RootElement.GetProperty("errors");
        foreach (var field in new[] { "maxIntentosEntrega", "intervaloEntreIntentosMinutos",
                     "politicaDevolucionAlAgotarIntentos", "plazoEstandarMinutos", "plazoUrgenteMinutos",
                     "plazoValidacionPruebaMinutos", "requisitosPrueba", "motivos" })
            Assert.True(missingFields.TryGetProperty(field, out _), $"Missing field error {field}.");

        using var missingVersion = await PutJsonAsync(client, BasePath + "/borrador", """
            {"maxIntentosEntrega":null,"intervaloEntreIntentosMinutos":null,
             "politicaDevolucionAlAgotarIntentos":null,"plazoEstandarMinutos":null,
             "plazoUrgenteMinutos":null,"requisitosPrueba":[],"motivos":[]}
            """);
        Assert.Equal(HttpStatusCode.BadRequest, missingVersion.StatusCode);

        using var missingMotivesCollection = await PutJsonAsync(client, BasePath + "/borrador", """
            {"versionConcurrencia":1,"maxIntentosEntrega":null,"intervaloEntreIntentosMinutos":null,
             "politicaDevolucionAlAgotarIntentos":null,"plazoEstandarMinutos":null,
             "plazoUrgenteMinutos":null,"requisitosPrueba":[]}
            """);
        Assert.Equal(HttpStatusCode.BadRequest, missingMotivesCollection.StatusCode);

        using var invalidEnum = await PutJsonAsync(client, BasePath + "/borrador", """
            {"versionConcurrencia":1,"maxIntentosEntrega":null,"intervaloEntreIntentosMinutos":null,
             "politicaDevolucionAlAgotarIntentos":"Inventada","plazoEstandarMinutos":null,
             "plazoUrgenteMinutos":null,"requisitosPrueba":[],"motivos":[]}
            """);
        Assert.Equal(HttpStatusCode.BadRequest, invalidEnum.StatusCode);

        using var numericEnum = await PutJsonAsync(client, BasePath + "/borrador", """
            {"versionConcurrencia":1,"maxIntentosEntrega":null,"intervaloEntreIntentosMinutos":null,
             "politicaDevolucionAlAgotarIntentos":null,"plazoEstandarMinutos":null,
             "plazoUrgenteMinutos":null,"requisitosPrueba":[1],"motivos":[]}
            """);
        Assert.Equal(HttpStatusCode.BadRequest, numericEnum.StatusCode);

        using var unknownField = await PutJsonAsync(client, BasePath + "/borrador", """
            {"versionConcurrencia":1,"maxIntentosEntrega":null,"intervaloEntreIntentosMinutos":null,
             "politicaDevolucionAlAgotarIntentos":null,"plazoEstandarMinutos":null,
             "plazoUrgenteMinutos":null,"requisitosPrueba":[],"motivos":[],"operadorId":"11111111-1111-4111-8111-111111111111"}
            """);
        Assert.Equal(HttpStatusCode.BadRequest, unknownField.StatusCode);

        using var duplicateProof = await client.PutAsJsonAsync(BasePath + "/borrador", Configuration(1,
            requirements: ["FirmaEnPantalla", "FirmaEnPantalla"]));
        Assert.Equal(HttpStatusCode.BadRequest, duplicateProof.StatusCode);
        using var completeSave = await client.PutAsJsonAsync(BasePath + "/borrador", Configuration(1));
        Assert.Equal(HttpStatusCode.OK, completeSave.StatusCode);
        var completeDraft = await completeSave.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, completeDraft.GetProperty("versionConcurrencia").GetInt64());
        Assert.Equal(3, completeDraft.GetProperty("maxIntentosEntrega").GetInt32());
        Assert.Equal("DecisionOperativa", completeDraft.GetProperty("politicaDevolucionAlAgotarIntentos").GetString());
        Assert.Equal(60, completeDraft.GetProperty("plazoValidacionPruebaMinutos").GetInt32());
        using var nonPositiveDeadline = await client.PutAsJsonAsync(BasePath + "/borrador",
            Configuration(2, proofDeadline: 0));
        Assert.Equal(HttpStatusCode.BadRequest, nonPositiveDeadline.StatusCode);

        using var duplicateMotive = await client.PutAsJsonAsync(BasePath + "/borrador", Configuration(2,
            motives: [Motive("DOMICILIO_CERRADO", "Cerrado"), Motive("DOMICILIO_CERRADO", "También cerrado")]));
        Assert.Equal(HttpStatusCode.BadRequest, duplicateMotive.StatusCode);
        using var removedMotive = await client.PutAsJsonAsync(BasePath + "/borrador", Configuration(2, motives: []));
        Assert.Equal(HttpStatusCode.BadRequest, removedMotive.StatusCode);

        using var staleEdit = await client.PutAsJsonAsync(BasePath + "/borrador", Configuration(1, maxAttempts: 4));
        Assert.Equal(HttpStatusCode.Conflict, staleEdit.StatusCode);
        var beforePublish = await (await client.GetAsync(BasePath + "/borrador")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, beforePublish.GetProperty("versionConcurrencia").GetInt64());
        Assert.Equal(3, beforePublish.GetProperty("maxIntentosEntrega").GetInt32());

        using var publishedResponse = await client.PostAsJsonAsync(BasePath + "/borrador/publicar",
            new { versionConcurrencia = 2 });
        Assert.Equal(HttpStatusCode.OK, publishedResponse.StatusCode);
        var published = await publishedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(originalDraftId, published.GetProperty("versionReglasOperativasId").GetGuid());
        Assert.Equal("Publicada", published.GetProperty("estado").GetString());
        Assert.Equal(1, published.GetProperty("numeroVersionPublicada").GetInt32());
        Assert.Equal(60, published.GetProperty("plazoValidacionPruebaMinutos").GetInt32());

        using var copiedResponse = await client.PostAsync(BasePath + "/borrador", null);
        Assert.Equal(HttpStatusCode.Created, copiedResponse.StatusCode);
        var copy = await copiedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, copy.GetProperty("versionConcurrencia").GetInt64());
        Assert.Equal(JsonValueKind.Null, copy.GetProperty("numeroVersionPublicada").ValueKind);
        Assert.Equal(60, copy.GetProperty("plazoValidacionPruebaMinutos").GetInt32());
        Assert.Equal("DOMICILIO_CERRADO", copy.GetProperty("motivos")[0].GetProperty("codigo").GetString());

        using var editedCopy = await client.PutAsJsonAsync(BasePath + "/borrador", Configuration(0, proofDeadline: 90,
            motives: [Motive("DOMICILIO_CERRADO", "Descripción nueva", available: false)]));
        Assert.Equal(HttpStatusCode.OK, editedCopy.StatusCode);
        var editedDraft = await editedCopy.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(90, editedDraft.GetProperty("plazoValidacionPruebaMinutos").GetInt32());
        using var publicationDetail = await client.GetAsync($"{BasePath}/publicaciones/{originalDraftId}");
        Assert.Equal(HttpStatusCode.OK, publicationDetail.StatusCode);
        var original = await publicationDetail.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(60, original.GetProperty("plazoValidacionPruebaMinutos").GetInt32());
        Assert.Equal("Domicilio cerrado", original.GetProperty("motivos")[0].GetProperty("descripcion").GetString());
        Assert.True(original.GetProperty("motivos")[0].GetProperty("disponible").GetBoolean());

        using var history = await client.GetAsync(BasePath + "/publicaciones?pagina=1&tamanoPagina=1");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        var historyPage = await history.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, historyPage.GetProperty("total").GetInt32());
        Assert.Equal(originalDraftId, historyPage.GetProperty("items")[0]
            .GetProperty("versionReglasOperativasId").GetGuid());

        var operatorBEmail = $"cu04-operator-{Guid.NewGuid():N}@example.test";
        using var registration = await client.PostAsJsonAsync("/api/auth/register",
            new { email = operatorBEmail, password = AuthenticationTestData.Password });
        Assert.Equal(HttpStatusCode.Created, registration.StatusCode);
        await LoginAsync(client, operatorBEmail);
        using var organization = await client.PostAsJsonAsync("/api/alta-propia/operadores", new { nombre = "CU-04 operator B" });
        Assert.Equal(HttpStatusCode.Created, organization.StatusCode);
        await LoginAsync(client, operatorBEmail);
        using var bDraftResponse = await client.PostAsync(BasePath + "/borrador", null);
        Assert.Equal(HttpStatusCode.Created, bDraftResponse.StatusCode);
        using var bSave = await client.PutAsJsonAsync(BasePath + "/borrador", Configuration(0));
        Assert.Equal(HttpStatusCode.OK, bSave.StatusCode);
        var bDraft = await bSave.Content.ReadFromJsonAsync<JsonElement>();
        var bVersionId = bDraft.GetProperty("versionReglasOperativasId").GetGuid();
        using var bPublication = await client.PostAsJsonAsync(BasePath + "/borrador/publicar",
            new { versionConcurrencia = 1 });
        Assert.Equal(HttpStatusCode.OK, bPublication.StatusCode);

        await LoginAsync(client, operatorEmail);
        using var foreign = await client.GetAsync($"{BasePath}/publicaciones/{bVersionId}");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using var statusAfterReads = await client.GetAsync(BasePath);
        var finalState = await statusAfterReads.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(finalState.GetProperty("configurado").GetBoolean());
        Assert.Equal(originalDraftId, finalState.GetProperty("publicacionVigente")
            .GetProperty("versionReglasOperativasId").GetGuid());
    }

    [Fact]
    public async Task AdministrativeRoutesKeepThePersistedOperatorAuthorizationBoundary()
    {
        var database = await postgres.CreateDatabaseAsync();
        string operatorEmail;
        string commerceEmail;
        string noPermissionEmail;
        string platformEmail;
        Guid operatorId;
        using (var bootstrap = new IntegrationApiFactory(database, seedOperationalRules: false))
        using (bootstrap.CreateHttpClient())
        {
            await using var db = database.CreateDbContext();
            var operatorAccess = await db.AccesosUsuario.Where(x => x.OperadorId != null).FirstAsync();
            operatorId = operatorAccess.OperadorId!.Value;
            operatorEmail = await db.Usuarios.Where(x => x.Id == operatorAccess.UsuarioId)
                .Select(x => x.Email).SingleAsync();
            var commerceAccess = await db.AccesosUsuario.FirstAsync(x => x.ComercioId != null);
            commerceEmail = await db.Usuarios.Where(x => x.Id == commerceAccess.UsuarioId)
                .Select(x => x.Email).SingleAsync();
            var commerceRole = await db.Roles.SingleAsync(x => x.Id == commerceAccess.RolId);
            var permission = await db.Permisos.SingleAsync(x => x.Codigo == "configuracion.gestionar");
            if (!await db.RolesPermisos.AnyAsync(x => x.RolId == commerceRole.Id && x.PermisoId == permission.Id))
                db.RolesPermisos.Add(new RolPermiso(commerceRole.Id, permission.Id));
            var operatorRole = await db.Roles.SingleAsync(x => x.OperadorId == operatorId && x.Codigo == "usuario_operador");
            noPermissionEmail = $"cu04-no-permission-{Guid.NewGuid():N}@example.test";
            var noPermissionUser = new Usuario(noPermissionEmail, AuthenticationTestData.Hash(noPermissionEmail));
            db.AddRange(noPermissionUser, new AccesoUsuario(noPermissionUser.Id, operatorId, null, operatorRole));
            platformEmail = $"cu04-platform-{Guid.NewGuid():N}@example.test";
            await AuthenticationTestData.PlatformRepository(db).InicializarAsync(platformEmail, AuthenticationTestData.Password);
            await db.SaveChangesAsync();
        }

        using var factory = new IntegrationApiFactory(database,
            settings: Settings(await CreateRestrictedApiConnectionAsync(database)), replaceDbContext: false);
        using var anonymous = factory.CreateHttpClient();
        using var anonymousResponse = await anonymous.GetAsync(BasePath);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var noPermission = factory.CreateHttpClient();
        await LoginAsync(noPermission, noPermissionEmail);
        using var forbidden = await noPermission.GetAsync(BasePath);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using var commerce = factory.CreateHttpClient();
        await LoginAsync(commerce, commerceEmail);
        using var commerceForbidden = await commerce.GetAsync(BasePath);
        Assert.Equal(HttpStatusCode.Forbidden, commerceForbidden.StatusCode);

        using var platform = factory.CreateHttpClient();
        await LoginAsync(platform, platformEmail);
        using var platformForbidden = await platform.GetAsync(BasePath);
        Assert.Equal(HttpStatusCode.Forbidden, platformForbidden.StatusCode);

        using var identityOnly = factory.CreateHttpClient();
        var identityEmail = $"cu04-identity-{Guid.NewGuid():N}@example.test";
        using var registration = await identityOnly.PostAsJsonAsync("/api/auth/register",
            new { email = identityEmail, password = AuthenticationTestData.Password });
        Assert.Equal(HttpStatusCode.Created, registration.StatusCode);
        await LoginAsync(identityOnly, identityEmail);
        using var noOrganization = await identityOnly.GetAsync(BasePath);
        Assert.Equal(HttpStatusCode.Forbidden, noOrganization.StatusCode);

        using var authorized = factory.CreateHttpClient();
        await LoginAsync(authorized, operatorEmail);
        using var allowed = await authorized.GetAsync(BasePath);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        await using (var db = database.CreateDbContext())
        {
            var operatorEntity = await db.Operadores.SingleAsync(x => x.Id == operatorId);
            operatorEntity.CambiarEstado(EstadoOrganizacion.Suspendida);
            await db.SaveChangesAsync();
        }
        using var suspended = await authorized.GetAsync(BasePath);
        Assert.Equal(HttpStatusCode.Unauthorized, suspended.StatusCode);
    }

    [Fact]
    public async Task OpenApiContainsTypedRulesRoutesAndExportsTheGeneratedDocument()
    {
        var database = await postgres.CreateDatabaseAsync();
        var settings = Settings(await CreateRestrictedApiConnectionAsync(database));
        using var factory = new IntegrationApiFactory(database, environment: "Development", settings: settings,
            replaceDbContext: false);
        using var client = factory.CreateHttpClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var paths = document.RootElement.GetProperty("paths");
        var expected = new Dictionary<string, string[]>
        {
            [BasePath] = ["get"],
            [BasePath + "/borrador"] = ["get", "post", "put"],
            [BasePath + "/borrador/publicar"] = ["post"],
            [BasePath + "/publicaciones"] = ["get"],
            [BasePath + "/publicaciones/{versionId}"] = ["get"],
            ["/api/rutas/{id}/cancelacion"] = ["post"]
        };
        foreach (var (path, methods) in expected)
        {
            Assert.True(paths.TryGetProperty(path, out var item), $"OpenAPI path {path} is missing.");
            foreach (var method in methods)
                Assert.True(item.TryGetProperty(method, out _), $"OpenAPI operation {method} {path} is missing.");
        }

        var exportPath = Environment.GetEnvironmentVariable("NEXORUTA_OPENAPI_EXPORT_PATH");
        if (string.IsNullOrWhiteSpace(exportPath)) return;
        var root = FindRepositoryRoot();
        var expectedPath = Path.GetFullPath(Path.Combine(root, "frontend", "contracts", "nexoruta.openapi.json"));
        if (!string.Equals(Path.GetFullPath(exportPath), expectedPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("OpenAPI export path must be the versioned frontend contract.");
        await File.WriteAllTextAsync(expectedPath, json);
    }

    private static object Configuration(long version, int? maxAttempts = 3, int? interval = 1440,
        string? policy = "DecisionOperativa", int? standard = 4320, int? urgent = 1440,
        string[]? requirements = null, object[]? motives = null, int? proofDeadline = 60)
        => new
        {
            versionConcurrencia = version,
            maxIntentosEntrega = maxAttempts,
            intervaloEntreIntentosMinutos = interval,
            politicaDevolucionAlAgotarIntentos = policy,
            plazoEstandarMinutos = standard,
            plazoUrgenteMinutos = urgent,
            plazoValidacionPruebaMinutos = proofDeadline,
            requisitosPrueba = requirements ?? ["FirmaEnPantalla", "NombreReceptor"],
            motivos = motives ?? [Motive("DOMICILIO_CERRADO", "Domicilio cerrado")]
        };

    private static object Motive(string code, string description, bool available = true)
        => new { codigo = code, descripcion = description, disponible = available };

    private static Dictionary<string, string?> Settings(string restrictedConnection) => new()
    {
        ["ConnectionStrings:Postgres"] = restrictedConnection,
        ["DatabaseInitialization:ApplyMigrations"] = "false",
        ["DatabaseInitialization:SeedData"] = "false"
    };

    private static async Task<HttpResponseMessage> PutJsonAsync(HttpClient client, string path, string json)
        => await client.PutAsync(path, new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

    private static async Task LoginAsync(HttpClient client, string email)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { email, password = AuthenticationTestData.Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static async Task<string> CreateRestrictedApiConnectionAsync(IntegrationDatabase database)
    {
        var role = $"cu04_http_{Guid.NewGuid():N}";
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
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

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "backend", "src", "NexoRuta.Api", "NexoRuta.Api.csproj")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate the NexoRuta repository root from the test output directory.");
    }
}
