using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using NexoRuta.Application.ZonasCobertura;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.ZonasCobertura;
using NexoRuta.Infrastructure.ZonasCobertura;
using NexoRuta.IntegrationTests.Fixtures;
using Npgsql;

namespace NexoRuta.IntegrationTests.ZonasCobertura;

public sealed class UruguayCatalogAndTerritoryTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task EmbeddedCatalogImportsAllDepartmentsAtomicallyAndIsIdempotent()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        var importer = new DepartamentoCatalogImporter(db);

        var first = await importer.ImportarCatalogoIncluidoAsync();
        var references = await db.DepartamentosReferencia.AsNoTracking().OrderBy(x => x.Codigo).ToListAsync();
        Assert.Equal(19, first.DepartmentCount);
        Assert.False(first.AlreadyImported);
        Assert.Equal(19, references.Count);
        Assert.Equal(19, references.Select(x => x.Codigo).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(new[]
        {
            "UY-AR", "UY-CA", "UY-CL", "UY-CO", "UY-DU", "UY-FD", "UY-FS", "UY-LA", "UY-MA",
            "UY-MO", "UY-PA", "UY-RN", "UY-RO", "UY-RV", "UY-SA", "UY-SJ", "UY-SO", "UY-TA", "UY-TT"
        }, references.Select(x => x.Codigo));
        Assert.All(references, reference =>
        {
            Assert.StartsWith("UY-", reference.Codigo, StringComparison.Ordinal);
            Assert.StartsWith("sha256:", reference.VersionFuente, StringComparison.Ordinal);
            Assert.Equal(4326, reference.Geometria.SRID);
            Assert.False(reference.Geometria.IsEmpty);
            Assert.True(reference.Geometria.IsValid);
            Assert.Contains("sit.mvot.gub.uy", reference.UrlFuente, StringComparison.Ordinal);
            Assert.Contains("IGM", reference.Atribucion, StringComparison.Ordinal);
            Assert.Equal(string.Empty, reference.Licencia);
        });
        var importedAt = references.Select(x => x.ImportadaUtc).Distinct().Single();
        var sourceVersion = references.Select(x => x.VersionFuente).Distinct(StringComparer.Ordinal).Single();

        var second = await importer.ImportarCatalogoIncluidoAsync();
        Assert.True(second.AlreadyImported);
        Assert.Equal(first.VersionFuente, second.VersionFuente);
        Assert.Equal(sourceVersion, second.VersionFuente);
        Assert.Equal(19, await db.DepartamentosReferencia.CountAsync());
        Assert.All(await db.DepartamentosReferencia.AsNoTracking().ToListAsync(), x => Assert.Equal(importedAt, x.ImportadaUtc));

        var alternateSource = JsonSerializer.Serialize(new
        {
            type = "FeatureCollection",
            features = references.Select(reference => new
            {
                type = "Feature",
                properties = new { DEPTO = reference.Nombre },
                geometry = new
                {
                    type = "Polygon",
                    coordinates = new[]
                    {
                        new[] { new[] { -56d, -34d }, new[] { -55.9d, -34d }, new[] { -55.9d, -33.9d }, new[] { -56d, -34d } }
                    }
                }
            })
        });
        await using var differentSource = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(alternateSource));
        await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportarAsync(differentSource));
        Assert.Equal(19, await db.DepartamentosReferencia.CountAsync());
        Assert.All(await db.DepartamentosReferencia.AsNoTracking().ToListAsync(), x => Assert.Equal(importedAt, x.ImportadaUtc));

        await using var explicitUpdate = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(alternateSource));
        var updated = await importer.ImportarAsync(explicitUpdate, permitirActualizacionExplicita: true);
        Assert.False(updated.AlreadyImported);
        Assert.Equal(19, updated.DepartmentCount);
        Assert.Equal(38, await db.DepartamentosReferencia.CountAsync());
    }

    [Fact]
    public async Task InvalidOrIncompleteCatalogWritesNothing()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        var importer = new DepartamentoCatalogImporter(db);
        await using var invalid = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            "{\"type\":\"FeatureCollection\",\"features\":[]}"));

        await Assert.ThrowsAsync<InvalidDataException>(() => importer.ImportarAsync(invalid));
        Assert.Empty(await db.DepartamentosReferencia.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task EmptyReferenceTableStillServesOfficialTerritoryAndCreatesCustomZone()
    {
        var database = await postgres.CreateDatabaseAsync();
        string operatorEmail;
        using (var bootstrapFactory = new IntegrationApiFactory(database))
        using (bootstrapFactory.CreateHttpClient())
        {
            await using var db = database.CreateDbContext();
            operatorEmail = await db.AccesosUsuario.Where(x => x.OperadorId != null)
                .Join(db.Usuarios, x => x.UsuarioId, x => x.Id, (_, user) => user.Email).FirstAsync();
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

        using var catalog = await client.GetAsync("/api/configuracion/zonas/departamentos");
        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
        Assert.Empty(await catalog.Content.ReadFromJsonAsync<JsonElement[]>() ?? []);

        using var territory = await client.GetAsync("/api/configuracion/zonas/departamentos/territorio");
        Assert.Equal(HttpStatusCode.OK, territory.StatusCode);
        var territoryData = await territory.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("UY", territoryData.GetProperty("codigoPais").GetString());
        Assert.StartsWith("sha256:", territoryData.GetProperty("versionFuente").GetString());

        using var department = await client.GetAsync("/api/configuracion/zonas/departamentos/UY-MO");
        Assert.Equal(HttpStatusCode.OK, department.StatusCode);
        var departmentData = await department.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("UY-MO", departmentData.GetProperty("codigo").GetString());

        using var create = await client.PostAsJsonAsync("/api/configuracion/zonas", new
        {
            nombre = "Geometría propia sin catálogo",
            geometria = departmentData.GetProperty("geometria"),
            codigoDepartamento = (string?)null
        });
        Assert.True(create.StatusCode == HttpStatusCode.Created,
            $"Expected custom geometry creation; received {(int)create.StatusCode}: {await create.Content.ReadAsStringAsync()}");
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Dibujada", created.GetProperty("origenGeometria").GetString());
        var createdId = created.GetProperty("id").GetGuid();
        var recovered = await client.GetFromJsonAsync<JsonElement>($"/api/configuracion/zonas/{createdId}");
        Assert.Equal("Geometría propia sin catálogo", recovered.GetProperty("nombre").GetString());

        using var slot = await client.PostAsJsonAsync($"/api/configuracion/zonas/{createdId}/franjas",
            new { diaSemanaIso = 1, horaInicio = "09:00", horaFin = "17:00", versionZona = 0 });
        Assert.Equal(HttpStatusCode.Created, slot.StatusCode);
        using var activation = await client.PutAsJsonAsync($"/api/configuracion/zonas/{createdId}/estado",
            new { estado = "Activa", version = 1 });
        Assert.Equal(HttpStatusCode.OK, activation.StatusCode);

        using var draft = await client.PostAsync("/api/configuracion/tarifas/borrador", null);
        Assert.Equal(HttpStatusCode.Created, draft.StatusCode);
        using var configuredRate = await client.PutAsJsonAsync("/api/configuracion/tarifas/borrador/tarifas", new
        {
            versionConcurrencia = 0,
            tarifas = new[]
            {
                new
                {
                    zonaId = createdId,
                    modalidad = "Estandar",
                    importeBaseUyu = 150m,
                    precioPorKilogramoUyu = 12m,
                    precioPorMetroCubicoUyu = 0m,
                    ajustes = Array.Empty<object>()
                }
            }
        });
        Assert.Equal(HttpStatusCode.OK, configuredRate.StatusCode);
        var rateResult = await configuredRate.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(createdId, rateResult.GetProperty("tarifas").EnumerateArray().Single()
            .GetProperty("zonaId").GetGuid());

        using var copied = await client.PostAsJsonAsync("/api/configuracion/zonas", new
        {
            nombre = "Montevideo copiado sin catálogo",
            geometria = (object?)null,
            codigoDepartamento = "UY-MO"
        });
        Assert.Equal(HttpStatusCode.Created, copied.StatusCode);
        var copiedData = await copied.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Departamento", copiedData.GetProperty("origenGeometria").GetString());
        Assert.Equal("UY-MO", copiedData.GetProperty("codigoDepartamento").GetString());
        Assert.Equal(territoryData.GetProperty("versionFuente").GetString(), copiedData.GetProperty("versionFuente").GetString());

        using var outside = await client.PostAsJsonAsync("/api/configuracion/zonas", new
        {
            nombre = "Fuera de Uruguay",
            geometria = new
            {
                type = "Polygon",
                coordinates = new[]
                {
                    new[] { new[] { -70d, -34d }, new[] { -69d, -34d },
                        new[] { -69d, -33d }, new[] { -70d, -34d } }
                }
            },
            codigoDepartamento = (string?)null
        });
        Assert.Equal(HttpStatusCode.BadRequest, outside.StatusCode);

        await using var verification = database.CreateDbContext();
        Assert.Empty(await verification.DepartamentosReferencia.AsNoTracking().ToListAsync());
        Assert.Equal(2, await verification.ZonasCobertura.CountAsync());
    }

    [Fact]
    public async Task ReferenceComponentsCoverDepartmentInPostgisAndApplicationValidation()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        await new DepartamentoCatalogImporter(db).ImportarCatalogoIncluidoAsync();
        var repository = new EfZonasCoberturaRepository(db);
        var territory = await repository.ObtenerTerritorioUruguayAsync();
        Assert.NotNull(territory);
        var departments = await db.DepartamentosReferencia.AsNoTracking().ToListAsync();
        var montevideo = departments.Single(x => x.Codigo == "UY-MO");
        var postgisCoversDepartment = await db.Database.SqlQueryRaw<bool>("""
            SELECT ST_Covers(country."Geometria", department."Geometria") AS "Value"
            FROM "DepartamentosReferencia" AS department
            CROSS JOIN (SELECT ST_Collect("Geometria") AS "Geometria" FROM "DepartamentosReferencia") AS country
            WHERE department."Codigo" = 'UY-MO'
            """).SingleAsync();
        Assert.True(postgisCoversDepartment);
        Assert.Equal(ResultadoValidacionTerritorial.Cubierta,
            await repository.ValidarTerritorioUruguayAsync(montevideo.Geometria));
    }

    [Fact]
    public async Task RestrictedHttpCatalogSupportsDepartmentCreationAndRejectsOutsidePointAndGeometryAtomically()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using (var importDb = database.CreateDbContext())
            await new DepartamentoCatalogImporter(importDb).ImportarCatalogoIncluidoAsync();

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

        var crossing = await CreateCrossingPolygonAsync(database);
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

        using var catalogResponse = await client.GetAsync("/api/configuracion/zonas/departamentos");
        Assert.Equal(HttpStatusCode.OK, catalogResponse.StatusCode);
        var catalog = await catalogResponse.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.NotNull(catalog);
        Assert.Equal(19, catalog.Length);

        using var territoryResponse = await client.GetAsync("/api/configuracion/zonas/departamentos/territorio");
        Assert.Equal(HttpStatusCode.OK, territoryResponse.StatusCode);
        Assert.True(territoryResponse.Headers.ETag?.Tag?.Contains("sha256:", StringComparison.Ordinal) == true);
        var territoryJson = await territoryResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("UY", territoryJson.GetProperty("codigoPais").GetString());
        Assert.Equal("MultiPolygon", territoryJson.GetProperty("geometria").GetProperty("type").GetString());

        using var departmentResponse = await client.GetAsync("/api/configuracion/zonas/departamentos/UY-MO");
        Assert.Equal(HttpStatusCode.OK, departmentResponse.StatusCode);
        var department = await departmentResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("UY-MO", department.GetProperty("codigo").GetString());
        Assert.Equal("MultiPolygon", department.GetProperty("geometria").GetProperty("type").GetString());

        using var sanJoseResponse = await client.GetAsync("/api/configuracion/zonas/departamentos/UY-SJ");
        Assert.Equal(HttpStatusCode.OK, sanJoseResponse.StatusCode);
        var sanJose = await sanJoseResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("UY-SJ", sanJose.GetProperty("codigo").GetString());
        Assert.Equal("SAN JOSÉ", sanJose.GetProperty("nombre").GetString());
        Assert.Equal("MultiPolygon", sanJose.GetProperty("geometria").GetProperty("type").GetString());

        using var createdResponse = await client.PostAsJsonAsync("/api/configuracion/zonas", new
        { nombre = "Cobertura desde Montevideo", codigoDepartamento = "UY-MO" });
        Assert.True(createdResponse.StatusCode == HttpStatusCode.Created,
            $"Expected department-based zone creation to succeed but got {(int)createdResponse.StatusCode}: {await createdResponse.Content.ReadAsStringAsync()}");
        Assert.NotNull(createdResponse.Headers.Location);
        var created = await createdResponse.Content.ReadFromJsonAsync<JsonElement>();
        var zoneId = created.GetProperty("id").GetGuid();
        Assert.Equal("Inactiva", created.GetProperty("estado").GetString());
        Assert.Equal("Departamento", created.GetProperty("origenGeometria").GetString());

        using var invalidCreate = await client.PostAsJsonAsync("/api/configuracion/zonas", new
        { nombre = "Fuera de Uruguay", geometria = ToGeoJson(crossing) });
        Assert.Equal(HttpStatusCode.BadRequest, invalidCreate.StatusCode);
        Assert.DoesNotContain("coordinates", await invalidCreate.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        using var invalidUpdate = await client.PutAsJsonAsync($"/api/configuracion/zonas/{zoneId}", new
        { nombre = "Cambio parcial no permitido", geometria = ToGeoJson(crossing), version = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, invalidUpdate.StatusCode);
        var afterRejectedUpdate = await client.GetFromJsonAsync<JsonElement>($"/api/configuracion/zonas/{zoneId}");
        Assert.Equal("Cobertura desde Montevideo", afterRejectedUpdate.GetProperty("nombre").GetString());
        Assert.Equal(0, afterRejectedUpdate.GetProperty("version").GetInt64());
        Assert.Equal(1, afterRejectedUpdate.GetProperty("revisionActual").GetInt32());

        var outOfTerritory = await CreateOutOfTerritoryZoneAsync(database, operatorId, crossing);
        using var invalidActivation = await client.PutAsJsonAsync($"/api/configuracion/zonas/{outOfTerritory}/estado",
            new { estado = "Activa", version = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, invalidActivation.StatusCode);
        var afterRejectedActivation = await client.GetFromJsonAsync<JsonElement>($"/api/configuracion/zonas/{outOfTerritory}");
        Assert.Equal("Inactiva", afterRejectedActivation.GetProperty("estado").GetString());
        Assert.Equal(0, afterRejectedActivation.GetProperty("version").GetInt64());
        Assert.Equal(1, afterRejectedActivation.GetProperty("revisionActual").GetInt32());

        await AssertTerritoryPointResponseAsync(client, database, valid: true, boundary: false);
        await AssertTerritoryPointResponseAsync(client, database, valid: true, boundary: true);
        await AssertTerritoryPointResponseAsync(client, database, valid: false);
    }

    private static async Task AssertTerritoryPointResponseAsync(
        HttpClient client, IntegrationDatabase database, bool valid, bool boundary = false)
    {
        Point point;
        await using (var db = database.CreateDbContext())
        {
            var geometries = await db.DepartamentosReferencia.AsNoTracking().Select(x => x.Geometria).ToListAsync();
            var territory = NetTopologySuite.Operation.Union.UnaryUnionOp.Union(geometries.Cast<Geometry>());
            var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
            point = valid
                ? factory.CreatePoint(boundary ? territory.Boundary.Coordinates[0] : territory.InteriorPoint.Coordinate)
                : FindOutsidePointInsideEnvelope(territory, factory);
            Assert.Equal(valid, territory.Covers(point));
        }

        using var response = await client.PostAsJsonAsync("/api/configuracion/zonas/cobertura", new
        { longitud = point.X, latitud = point.Y });
        Assert.Equal(valid ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static Point FindOutsidePointInsideEnvelope(Geometry territory, GeometryFactory factory)
    {
        var bounds = territory.EnvelopeInternal;
        for (var xIndex = 1; xIndex < 40; xIndex++)
        for (var yIndex = 1; yIndex < 40; yIndex++)
        {
            var point = factory.CreatePoint(new Coordinate(
                bounds.MinX + (bounds.Width * xIndex / 40d),
                bounds.MinY + (bounds.Height * yIndex / 40d)));
            if (!territory.Covers(point)) return point;
        }
        throw new InvalidOperationException("The reference bounding box did not contain a point outside Uruguay.");
    }

    private static async Task<Polygon> CreateCrossingPolygonAsync(IntegrationDatabase database)
    {
        await using var db = database.CreateDbContext();
        var departments = await db.DepartamentosReferencia.AsNoTracking().Select(x => x.Geometria).ToListAsync();
        var territory = NetTopologySuite.Operation.Union.UnaryUnionOp.Union(departments.Cast<Geometry>());
        var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        var interiorPoints = departments.Select(x => x.InteriorPoint.Coordinate).ToList();
        var sample = new Random(20261019);
        var bounds = territory.EnvelopeInternal;
        for (var index = 0; index < 100; index++)
        {
            var candidate = factory.CreatePoint(new Coordinate(
                bounds.MinX + (sample.NextDouble() * bounds.Width),
                bounds.MinY + (sample.NextDouble() * bounds.Height)));
            if (territory.Contains(candidate)) interiorPoints.Add(candidate.Coordinate);
        }

        for (var first = 0; first < interiorPoints.Count; first++)
        for (var second = first + 1; second < interiorPoints.Count; second++)
        {
            var segment = factory.CreateLineString([interiorPoints[first], interiorPoints[second]]);
            if (territory.Covers(segment)) continue;
            for (var third = 0; third < interiorPoints.Count; third++)
            {
                if (third == first || third == second) continue;
                var vertices = new[] { interiorPoints[first], interiorPoints[second], interiorPoints[third] };
                var coordinates = new[] { vertices[0], vertices[1], vertices[2], vertices[0] };
                var polygon = factory.CreatePolygon(factory.CreateLinearRing(coordinates));
                if (!polygon.IsValid || !vertices.All(point => territory.Covers(factory.CreatePoint(point)))) continue;
                if (!territory.Covers(polygon)) return polygon;
            }
        }
        throw new InvalidOperationException("No interior-vertex polygon crossing the reference boundary was found.");
    }

    private static string ToGeoJson(Polygon geometry) => JsonSerializer.Serialize(new
    {
        type = "Polygon",
        coordinates = new[] { geometry.ExteriorRing.Coordinates.Select(x => new[] { x.X, x.Y }) }
    });

    private static async Task<Guid> CreateOutOfTerritoryZoneAsync(IntegrationDatabase database, Guid operatorId, Polygon geometry)
    {
        await using var db = database.CreateDbContext();
        var zone = ZonaCobertura.Crear(operatorId, "Legacy out of territory", geometry,
            franjasIniciales: [new(DiaSemanaIso.Lunes, new TimeOnly(9, 0), new TimeOnly(10, 0))]);
        await new EfZonasCoberturaRepository(db).GuardarNuevaAsync(zone, Guid.CreateVersion7());
        return zone.Id;
    }

    private static async Task<string> CreateRestrictedApiConnectionAsync(IntegrationDatabase database)
    {
        var role = $"cu02_catalog_{Guid.NewGuid():N}";
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

    private static async Task LoginAsync(HttpClient client, string email)
    {
        using var login = await client.PostAsJsonAsync("/api/auth/login",
            new { email, password = AuthenticationTestData.Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}
