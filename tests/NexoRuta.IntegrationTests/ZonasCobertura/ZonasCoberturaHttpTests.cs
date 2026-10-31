using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Newtonsoft.Json;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.ZonasCobertura;
using NexoRuta.IntegrationTests.Fixtures;
using Npgsql;

namespace NexoRuta.IntegrationTests.ZonasCobertura;

public sealed class ZonasCoberturaHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Polygon = """{"type":"Polygon","coordinates":[[[-56,-34],[-55,-34],[-55,-33],[-56,-33],[-56,-34]]]}""";

    [Fact]
    public async Task OperatorManagesZonesAndSlotsAndQueriesAllBoundaryMatchesThroughHttp()
    {
        var database = await postgres.CreateDatabaseAsync();
        string operatorEmail;
        Guid operatorId;
        using (var bootstrapFactory = new IntegrationApiFactory(database))
        using (bootstrapFactory.CreateHttpClient())
        {
            await using var db = database.CreateDbContext();
            operatorEmail = await db.AccesosUsuario.Where(x => x.OperadorId != null)
                .Join(db.Usuarios, x => x.UsuarioId, x => x.Id, (_, user) => user.Email).FirstAsync();
            operatorId = (await db.AccesosUsuario.FirstAsync(x => x.OperadorId != null)).OperadorId!.Value;
        }

        var runtimeConnection = await CreateRestrictedApiConnectionAsync(database);
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = runtimeConnection,
            ["DatabaseInitialization:ApplyMigrations"] = "false",
            ["DatabaseInitialization:SeedData"] = "false"
        };
        using var factory = new IntegrationApiFactory(database, settings: settings, replaceDbContext: false);
        using var client = factory.CreateHttpClient();
        await LoginAsync(client, operatorEmail);

        using var anonymous = factory.CreateHttpClient();
        using var anonymousRead = await anonymous.GetAsync("/api/configuracion/zonas");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousRead.StatusCode);

        using var noSource = await client.PostAsJsonAsync("/api/configuracion/zonas", new { nombre = "None" });
        Assert.Equal(HttpStatusCode.BadRequest, noSource.StatusCode);
        using var bothSources = await client.PostAsJsonAsync("/api/configuracion/zonas", new
        { nombre = "Both", geometria = JsonDocument.Parse(Polygon).RootElement, codigoDepartamento = "XX" });
        Assert.Equal(HttpStatusCode.BadRequest, bothSources.StatusCode);

        using var catalog = await client.GetAsync("/api/configuracion/zonas/departamentos");
        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
        Assert.Empty((await catalog.Content.ReadFromJsonAsync<JsonElement[]>()) ?? []);
        using var territoryWithoutReference = await client.GetAsync("/api/configuracion/zonas/departamentos/territorio");
        Assert.Equal(HttpStatusCode.OK, territoryWithoutReference.StatusCode);
        Assert.Equal("UY", (await territoryWithoutReference.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("codigoPais").GetString());
        using var coverageWithoutReference = await client.PostAsJsonAsync("/api/configuracion/zonas/cobertura",
            new { longitud = -56d, latitud = -34d });
        Assert.Equal(HttpStatusCode.OK, coverageWithoutReference.StatusCode);
        Assert.Equal("SinCobertura", (await coverageWithoutReference.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("resultado").GetString());

        using var montevideoResponse = await client.GetAsync("/api/configuracion/zonas/departamentos/UY-MO");
        Assert.Equal(HttpStatusCode.OK, montevideoResponse.StatusCode);
        var montevideo = await montevideoResponse.Content.ReadFromJsonAsync<JsonElement>();
        var montevideoGeometry = montevideo.GetProperty("geometria").Clone();
        using var artigasResponse = await client.GetAsync("/api/configuracion/zonas/departamentos/UY-AR");
        Assert.Equal(HttpStatusCode.OK, artigasResponse.StatusCode);
        var artigas = await artigasResponse.Content.ReadFromJsonAsync<JsonElement>();
        var geometryFactory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        var geoJsonReader = new GeoJsonReader(geometryFactory, new JsonSerializerSettings());
        var montevideoShape = geoJsonReader.Read<Geometry>(montevideo.GetProperty("geometria").GetRawText());
        var artigasShape = geoJsonReader.Read<Geometry>(artigas.GetProperty("geometria").GetRawText());
        var boundaryPoint = montevideoShape.Boundary.Coordinate!;
        var uncoveredPoint = artigasShape.InteriorPoint.Coordinate!;

        using var missingDepartment = await client.PostAsJsonAsync("/api/configuracion/zonas", new
        { nombre = "Unknown department", codigoDepartamento = "XX" });
        Assert.Equal(HttpStatusCode.NotFound, missingDepartment.StatusCode);
        const string invalidPolygon = """{"type":"Polygon","coordinates":[[[0,0],[2,2],[0,2],[2,0],[0,0]]]}""";
        using var invalidGeometry = await client.PostAsync("/api/configuracion/zonas",
            new StringContent($"{{\"nombre\":\"Invalid\",\"geometria\":{invalidPolygon}}}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, invalidGeometry.StatusCode);
        Assert.DoesNotContain("coordinates", await invalidGeometry.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var firstId = await CreateZoneAsync(client, "West coverage", montevideoGeometry);
        using var firstDetailResponse = await client.GetAsync($"/api/configuracion/zonas/{firstId}");
        Assert.Equal(HttpStatusCode.OK, firstDetailResponse.StatusCode);
        var first = await firstDetailResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("MultiPolygon", first.GetProperty("geometria").GetProperty("type").GetString());
        Assert.Equal(0, first.GetProperty("version").GetInt64());
        Assert.Equal(1, first.GetProperty("revisionActual").GetInt32());

        using var activateWithoutSlots = await client.PutAsJsonAsync($"/api/configuracion/zonas/{firstId}/estado",
            new { estado = "Activa", version = 0 });
        Assert.Equal(HttpStatusCode.Conflict, activateWithoutSlots.StatusCode);

        using var createdSlot = await client.PostAsJsonAsync($"/api/configuracion/zonas/{firstId}/franjas",
            new { diaSemanaIso = 7, horaInicio = "23:30:00.123456", horaFin = "01:00:00.654321", versionZona = 0 });
        Assert.Equal(HttpStatusCode.Created, createdSlot.StatusCode);
        var slotResult = await createdSlot.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, slotResult.GetProperty("versionZona").GetInt64());
        var firstSlotId = slotResult.GetProperty("franja").GetProperty("id").GetGuid();
        Assert.Equal("23:30:00.123456", slotResult.GetProperty("franja").GetProperty("horaInicio").GetString());
        Assert.Equal("01:00:00.654321", slotResult.GetProperty("franja").GetProperty("horaFin").GetString());

        using var sundayMondayOverlap = await client.PostAsJsonAsync($"/api/configuracion/zonas/{firstId}/franjas",
            new { diaSemanaIso = 1, horaInicio = "00:30", horaFin = "01:30", versionZona = 1 });
        Assert.Equal(HttpStatusCode.Conflict, sundayMondayOverlap.StatusCode);

        using var activate = await client.PutAsJsonAsync($"/api/configuracion/zonas/{firstId}/estado",
            new { estado = "Activa", version = 1 });
        Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
        var activated = await activate.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, activated.GetProperty("version").GetInt64());

        using var stale = await client.PutAsJsonAsync($"/api/configuracion/zonas/{firstId}",
            new { nombre = "Stale update", geometria = montevideoGeometry, version = 1 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var afterStale = await client.GetAsync($"/api/configuracion/zonas/{firstId}");
        var unchanged = await afterStale.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("West coverage", unchanged.GetProperty("nombre").GetString());
        Assert.Equal(2, unchanged.GetProperty("version").GetInt64());

        var secondId = await CreateZoneAsync(client, "East coverage", montevideoGeometry);
        using var secondSlot = await client.PostAsJsonAsync($"/api/configuracion/zonas/{secondId}/franjas",
            new { diaSemanaIso = 2, horaInicio = "10:00", horaFin = "12:00", versionZona = 0 });
        Assert.Equal(HttpStatusCode.Created, secondSlot.StatusCode);
        using var secondActivate = await client.PutAsJsonAsync($"/api/configuracion/zonas/{secondId}/estado",
            new { estado = "Activa", version = 1 });
        Assert.Equal(HttpStatusCode.OK, secondActivate.StatusCode);

        using var sanJoseCreated = await client.PostAsJsonAsync("/api/configuracion/zonas", new
        { nombre = "San Jose regression", codigoDepartamento = "UY-SJ" });
        Assert.Equal(HttpStatusCode.Created, sanJoseCreated.StatusCode);
        var sanJose = await sanJoseCreated.Content.ReadFromJsonAsync<JsonElement>();
        var sanJoseId = sanJose.GetProperty("id").GetGuid();
        using var sanJoseSlot = await client.PostAsJsonAsync($"/api/configuracion/zonas/{sanJoseId}/franjas",
            new { diaSemanaIso = 3, horaInicio = "13:00", horaFin = "14:00", versionZona = 0 });
        Assert.Equal(HttpStatusCode.Created, sanJoseSlot.StatusCode);
        using var sanJoseActivate = await client.PutAsJsonAsync($"/api/configuracion/zonas/{sanJoseId}/estado",
            new { estado = "Activa", version = 1 });
        Assert.Equal(HttpStatusCode.OK, sanJoseActivate.StatusCode);
        using var sanJoseRead = await client.GetAsync($"/api/configuracion/zonas/{sanJoseId}");
        Assert.Equal(HttpStatusCode.OK, sanJoseRead.StatusCode);
        var sanJoseState = await sanJoseRead.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Activa", sanJoseState.GetProperty("estado").GetString());
        Assert.Equal(2, sanJoseState.GetProperty("version").GetInt64());
        Assert.Equal(3, sanJoseState.GetProperty("revisionActual").GetInt32());
        using var sanJoseStale = await client.PutAsJsonAsync($"/api/configuracion/zonas/{sanJoseId}/estado",
            new { estado = "Inactiva", version = 1 });
        Assert.Equal(HttpStatusCode.Conflict, sanJoseStale.StatusCode);
        using var sanJoseAfterStale = await client.GetAsync($"/api/configuracion/zonas/{sanJoseId}");
        Assert.Equal("Activa", (await sanJoseAfterStale.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("estado").GetString());
        using var sanJoseDeactivate = await client.PutAsJsonAsync($"/api/configuracion/zonas/{sanJoseId}/estado",
            new { estado = "Inactiva", version = 2 });
        Assert.Equal(HttpStatusCode.OK, sanJoseDeactivate.StatusCode);
        using var sanJoseAfterDeactivate = await client.GetAsync($"/api/configuracion/zonas/{sanJoseId}");
        var sanJoseInactiveState = await sanJoseAfterDeactivate.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Inactiva", sanJoseInactiveState.GetProperty("estado").GetString());
        Assert.Equal(3, sanJoseInactiveState.GetProperty("version").GetInt64());
        using var wrongZoneSlot = await client.PutAsJsonAsync($"/api/configuracion/zonas/{secondId}/franjas/{firstSlotId}",
            new { diaSemanaIso = 2, horaInicio = "10:00", horaFin = "12:00", estado = "Activa", versionZona = 2 });
        Assert.Equal(HttpStatusCode.NotFound, wrongZoneSlot.StatusCode);

        using var onBoundary = await client.PostAsJsonAsync("/api/configuracion/zonas/cobertura",
            new { longitud = boundaryPoint.X, latitud = boundaryPoint.Y });
        Assert.Equal(HttpStatusCode.OK, onBoundary.StatusCode);
        var boundary = await onBoundary.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Cubierta", boundary.GetProperty("resultado").GetString());
        Assert.True(boundary.GetProperty("requiereSeleccionDeZona").GetBoolean());
        Assert.Equal(2, boundary.GetProperty("coincidencias").GetArrayLength());

        using var outside = await client.PostAsJsonAsync("/api/configuracion/zonas/cobertura",
            new { longitud = uncoveredPoint.X, latitud = uncoveredPoint.Y });
        var none = await outside.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("SinCobertura", none.GetProperty("resultado").GetString());
        Assert.Empty(none.GetProperty("coincidencias").EnumerateArray());
        using var invalidCoordinates = await client.PostAsJsonAsync("/api/configuracion/zonas/cobertura",
            new { longitud = 181, latitud = -33.5 });
        Assert.Equal(HttpStatusCode.BadRequest, invalidCoordinates.StatusCode);

        using var invalidStatus = await client.GetAsync("/api/configuracion/zonas?estado=unknown");
        Assert.Equal(HttpStatusCode.BadRequest, invalidStatus.StatusCode);
        using var protectedField = await client.PostAsJsonAsync("/api/configuracion/zonas", new
        { nombre = "Tampered", geometria = montevideoGeometry, operadorId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, protectedField.StatusCode);

        _ = await CreateZoneAsync(client, "Inactive zone", montevideoGeometry);
        var listed = await client.GetFromJsonAsync<JsonElement>("/api/configuracion/zonas");
        Assert.Equal(2, listed.GetProperty("total").GetInt32());
        using var allStates = await client.GetAsync("/api/configuracion/zonas?estado=Inactiva");
        var inactive = await allStates.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, inactive.GetProperty("total").GetInt32());

        using var slots = await client.GetAsync($"/api/configuracion/zonas/{firstId}/franjas");
        var slotList = await slots.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, slotList.GetProperty("items").GetArrayLength());
        Assert.Equal(2, slotList.GetProperty("versionZona").GetInt64());

        string commerceEmail;
        string noPermissionOperatorEmail;
        await using (var db = database.CreateDbContext())
        {
            var commerceAccess = await db.AccesosUsuario.FirstAsync(x => x.ComercioId != null);
            commerceEmail = await db.Usuarios.Where(x => x.Id == commerceAccess.UsuarioId).Select(x => x.Email).SingleAsync();
            var commerceRole = await db.Roles.SingleAsync(x => x.Id == commerceAccess.RolId);
            var configPermission = await db.Permisos.SingleAsync(x => x.Codigo == "configuracion.gestionar");
            if (!await db.RolesPermisos.AnyAsync(x => x.RolId == commerceRole.Id && x.PermisoId == configPermission.Id))
                db.RolesPermisos.Add(new RolPermiso(commerceRole.Id, configPermission.Id));

            noPermissionOperatorEmail = $"no-coverage-{Guid.NewGuid():N}@example.test";
            var restrictedRole = await db.Roles.SingleAsync(x => x.OperadorId == operatorId && x.Codigo == "usuario_operador");
            var restrictedUser = new Usuario(noPermissionOperatorEmail, AuthenticationTestData.Hash(noPermissionOperatorEmail));
            db.AddRange(restrictedUser, new AccesoUsuario(restrictedUser.Id, operatorId, null, restrictedRole));
            await db.SaveChangesAsync();
        }

        using var commerceClient = factory.CreateHttpClient();
        await LoginAsync(commerceClient, commerceEmail);
        using var commerceRead = await commerceClient.GetAsync("/api/configuracion/zonas");
        Assert.Equal(HttpStatusCode.Forbidden, commerceRead.StatusCode);
        using var restrictedOperator = factory.CreateHttpClient();
        await LoginAsync(restrictedOperator, noPermissionOperatorEmail);
        using var denied = await restrictedOperator.GetAsync("/api/configuracion/zonas");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        using var secondOperatorClient = factory.CreateHttpClient();
        var secondOperatorEmail = $"second-operator-{Guid.NewGuid():N}@example.test";
        using var registered = await secondOperatorClient.PostAsJsonAsync("/api/auth/register",
            new { email = secondOperatorEmail, password = AuthenticationTestData.Password });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        await LoginAsync(secondOperatorClient, secondOperatorEmail);
        using var organization = await secondOperatorClient.PostAsJsonAsync("/api/alta-propia/operadores", new { nombre = "Second operator" });
        Assert.Equal(HttpStatusCode.Created, organization.StatusCode);
        await LoginAsync(secondOperatorClient, secondOperatorEmail);
        var otherZoneId = await CreateZoneAsync(secondOperatorClient, "Private zone B", montevideoGeometry);
        using var foreign = await client.GetAsync($"/api/configuracion/zonas/{otherZoneId}");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        await using var verifyEmptyCatalog = database.CreateDbContext();
        Assert.Empty(await verifyEmptyCatalog.DepartamentosReferencia.AsNoTracking().ToListAsync());
    }

    private static async Task<Guid> CreateZoneAsync(HttpClient client, string name, JsonElement geometry)
    {
        using var response = await client.PostAsJsonAsync("/api/configuracion/zonas", new { nombre = name, geometria = geometry });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Inactiva", detail.GetProperty("estado").GetString());
        Assert.Equal(JsonValueKind.Object, detail.GetProperty("geometria").ValueKind);
        Assert.NotNull(response.Headers.Location);
        return detail.GetProperty("id").GetGuid();
    }

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
        var role = $"cu02_http_{Guid.NewGuid():N}";
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
}
