using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.ZonasCobertura;
using NexoRuta.Infrastructure.Administracion.Repositories;
using NexoRuta.IntegrationTests.Fixtures;
using Npgsql;

namespace NexoRuta.IntegrationTests.Tarifas;

public sealed class TarifarioHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task GeneratedOpenApiIncludesTarifarioRoutesAndExportsVersionedContractOnRequest()
    {
        var database = await postgres.CreateDatabaseAsync();
        using (var bootstrapFactory = new IntegrationApiFactory(database))
        using (bootstrapFactory.CreateHttpClient()) { }
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = await CreateRestrictedApiConnectionAsync(database),
            ["DatabaseInitialization:ApplyMigrations"] = "false",
            ["DatabaseInitialization:SeedData"] = "false"
        };
        using var factory = new IntegrationApiFactory(database, environment: "Development", settings: settings,
            replaceDbContext: false);
        using var client = factory.CreateHttpClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var paths = document.RootElement.GetProperty("paths");
        Assert.True(paths.GetProperty("/api/configuracion/tarifas").TryGetProperty("get", out _));
        Assert.True(paths.GetProperty("/api/configuracion/tarifas/borrador/tarifas").TryGetProperty("put", out _));
        Assert.True(paths.GetProperty("/api/configuracion/tarifas/simulaciones").TryGetProperty("post", out _));

        var exportPath = Environment.GetEnvironmentVariable("NEXORUTA_OPENAPI_EXPORT_PATH");
        if (!string.IsNullOrWhiteSpace(exportPath))
        {
            var root = FindRepositoryRoot();
            var expectedPath = Path.GetFullPath(Path.Combine(root, "frontend", "contracts", "nexoruta.openapi.json"));
            if (!string.Equals(Path.GetFullPath(exportPath), expectedPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("OpenAPI export path must be the versioned frontend contract.");
            await File.WriteAllTextAsync(expectedPath, json);
        }
    }

    [Fact]
    public async Task OperatorConfiguresSimulatesPublishesAndCopiesTarifarioThroughHttp()
    {
        var database = await postgres.CreateDatabaseAsync();
        string operatorEmail;
        Guid operatorId;
        Guid zoneId;
        Guid inactiveZoneId;
        using (var bootstrapFactory = new IntegrationApiFactory(database))
        using (bootstrapFactory.CreateHttpClient())
        {
            await using var db = database.CreateDbContext();
            var access = await db.AccesosUsuario.Where(x => x.OperadorId != null).FirstAsync();
            operatorId = access.OperadorId!.Value;
            operatorEmail = await db.Usuarios.Where(x => x.Id == access.UsuarioId).Select(x => x.Email).SingleAsync();
            var factory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
            var geometry = factory.CreateMultiPolygon([factory.CreatePolygon(
                [new(-56, -34), new(-55, -34), new(-55, -33), new(-56, -33), new(-56, -34)])]);
            var zone = ZonaCobertura.Crear(operatorId, "Tariff test zone", geometry,
                franjasIniciales: [new(DiaSemanaIso.Lunes, new TimeOnly(9, 0), new TimeOnly(10, 0))]);
            zone.Activar(zone.Version);
            var inactiveZone = ZonaCobertura.Crear(operatorId, "Inactive tariff test zone", geometry);
            db.ZonasCobertura.Add(zone);
            db.ZonasCobertura.Add(inactiveZone);
            await db.SaveChangesAsync();
            zoneId = zone.Id;
            inactiveZoneId = inactiveZone.Id;
        }

        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = await CreateRestrictedApiConnectionAsync(database),
            ["DatabaseInitialization:ApplyMigrations"] = "false",
            ["DatabaseInitialization:SeedData"] = "false"
        };
        using var factoryApi = new IntegrationApiFactory(database, settings: settings, replaceDbContext: false);
        using var client = factoryApi.CreateHttpClient();
        await LoginAsync(client, operatorEmail);

        using var initial = await client.GetAsync("/api/configuracion/tarifas");
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        var initialStatus = await initial.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(initialStatus.GetProperty("configurado").GetBoolean());
        Assert.Equal(JsonValueKind.Null, initialStatus.GetProperty("publicacionVigente").ValueKind);
        Assert.Equal(JsonValueKind.Null, initialStatus.GetProperty("borrador").ValueKind);

        using var create = await client.PostAsync("/api/configuracion/tarifas/borrador", null);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        Assert.NotNull(create.Headers.Location);
        var draft = await create.Content.ReadFromJsonAsync<JsonElement>();
        var draftId = draft.GetProperty("versionTarifarioId").GetGuid();
        Assert.Equal("Borrador", draft.GetProperty("estado").GetString());
        Assert.Equal(0, draft.GetProperty("versionConcurrencia").GetInt64());
        Assert.Empty(draft.GetProperty("tarifas").EnumerateArray());

        using var missingVersion = await client.PutAsJsonAsync("/api/configuracion/tarifas/borrador/tarifas", new
        {
            tarifas = Array.Empty<object>()
        });
        Assert.Equal(HttpStatusCode.BadRequest, missingVersion.StatusCode);
        using var unchangedInitialDraft = await client.GetAsync("/api/configuracion/tarifas/borrador");
        Assert.Equal(0, (await unchangedInitialDraft.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("versionConcurrencia").GetInt64());

        var rateBody = new
        {
            versionConcurrencia = 0,
            tarifas = new object[]
            {
                new
                {
                    zonaId = zoneId,
                    modalidad = "Estandar",
                    importeBaseUyu = 120m,
                    precioPorKilogramoUyu = 25.50m,
                    precioPorMetroCubicoUyu = 900m,
                    ajustes = new[]
                    {
                        new { nombre = "Surcharge fixed", tipo = "RecargoFijo", importeFijoUyu = (decimal?)12m, porcentaje = (decimal?)null },
                        new { nombre = "Surcharge percentage", tipo = "RecargoPorcentual", importeFijoUyu = (decimal?)null, porcentaje = (decimal?)15m },
                        new { nombre = "Bonus fixed", tipo = "BonificacionFija", importeFijoUyu = (decimal?)10m, porcentaje = (decimal?)null },
                        new { nombre = "Bonus percentage", tipo = "BonificacionPorcentual", importeFijoUyu = (decimal?)null, porcentaje = (decimal?)5m }
                    }
                },
                new
                {
                    zonaId = inactiveZoneId,
                    modalidad = "Urgente",
                    importeBaseUyu = 0m,
                    precioPorKilogramoUyu = 0m,
                    precioPorMetroCubicoUyu = 0m,
                    ajustes = Array.Empty<object>()
                }
            }
        };
        using var saved = await client.PutAsJsonAsync("/api/configuracion/tarifas/borrador/tarifas", rateBody);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var configured = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, configured.GetProperty("versionConcurrencia").GetInt64());
        Assert.Equal(2, configured.GetProperty("tarifas").GetArrayLength());
        Assert.Equal(4, configured.GetProperty("tarifas")[0].GetProperty("ajustes").GetArrayLength());

        using var tampered = await client.PutAsJsonAsync("/api/configuracion/tarifas/borrador/tarifas", new
        {
            versionConcurrencia = 1,
            operadorId = Guid.NewGuid(),
            tarifas = Array.Empty<object>()
        });
        Assert.Equal(HttpStatusCode.BadRequest, tampered.StatusCode);

        using var stale = await client.PutAsJsonAsync("/api/configuracion/tarifas/borrador/tarifas", rateBody);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var simulation = new
        {
            versionTarifarioId = draftId,
            zonaId = zoneId,
            modalidad = "Estandar",
            bultos = new[]
            {
                new { pesoGramos = 1500m, largoCentimetros = 40m, anchoCentimetros = 30m, altoCentimetros = 20m },
                new { pesoGramos = 2000m, largoCentimetros = 50m, anchoCentimetros = 20m, altoCentimetros = 10m }
            }
        };
        using var simulated = await client.PostAsJsonAsync("/api/configuracion/tarifas/simulaciones", simulation);
        Assert.Equal(HttpStatusCode.OK, simulated.StatusCode);
        var calculation = await simulated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Borrador", calculation.GetProperty("origen").GetString());
        Assert.Equal("UYU", calculation.GetProperty("moneda").GetString());
        Assert.Equal(3.5m, calculation.GetProperty("pesoTotalKg").GetDecimal());
        Assert.Equal(0.034m, calculation.GetProperty("volumenTotalM3").GetDecimal());
        Assert.Equal(239.85m, calculation.GetProperty("subtotalUyu").GetDecimal());
        Assert.Equal(47.9775m, calculation.GetProperty("recargosUyu").GetDecimal());
        Assert.Equal(21.9925m, calculation.GetProperty("bonificacionesUyu").GetDecimal());
        Assert.Equal(265.835m, calculation.GetProperty("totalPrevioPisoUyu").GetDecimal());
        Assert.Equal(265.84m, calculation.GetProperty("totalFinalUyu").GetDecimal());
        using var clientPricedSimulation = await client.PostAsJsonAsync("/api/configuracion/tarifas/simulaciones", new
        {
            versionTarifarioId = draftId,
            zonaId = zoneId,
            modalidad = "Estandar",
            importeBaseUyu = 0m,
            bultos = new[] { new { pesoGramos = 1m, largoCentimetros = 1m, anchoCentimetros = 1m, altoCentimetros = 1m } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, clientPricedSimulation.StatusCode);
        using var afterSimulation = await client.GetAsync("/api/configuracion/tarifas/borrador");
        var unchangedDraft = await afterSimulation.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, unchangedDraft.GetProperty("versionConcurrencia").GetInt64());

        using var publishedResponse = await client.PostAsJsonAsync("/api/configuracion/tarifas/borrador/publicar",
            new { versionConcurrencia = 1 });
        Assert.Equal(HttpStatusCode.OK, publishedResponse.StatusCode);
        var published = await publishedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Publicada", published.GetProperty("estado").GetString());
        Assert.Equal(1, published.GetProperty("numeroVersionPublicada").GetInt32());
        Assert.Equal(1, published.GetProperty("versionConcurrencia").GetInt64());
        using var immutablePublicationEdit = await client.PutAsJsonAsync(
            $"/api/configuracion/tarifas/publicaciones/{draftId}", rateBody);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, immutablePublicationEdit.StatusCode);

        simulation = new
        {
            versionTarifarioId = draftId,
            zonaId = zoneId,
            modalidad = "Estandar",
            bultos = new[]
            {
                new { pesoGramos = 1500m, largoCentimetros = 40m, anchoCentimetros = 30m, altoCentimetros = 20m },
                new { pesoGramos = 2000m, largoCentimetros = 50m, anchoCentimetros = 20m, altoCentimetros = 10m }
            }
        };
        using var publishedSimulation = await client.PostAsJsonAsync("/api/configuracion/tarifas/simulaciones", simulation);
        Assert.Equal(HttpStatusCode.OK, publishedSimulation.StatusCode);
        var publishedCalculation = await publishedSimulation.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Publicacion", publishedCalculation.GetProperty("origen").GetString());
        Assert.Equal(1, publishedCalculation.GetProperty("numeroVersionPublicada").GetInt32());
        using var inactiveZoneSimulation = await client.PostAsJsonAsync("/api/configuracion/tarifas/simulaciones", new
        {
            versionTarifarioId = draftId, zonaId = inactiveZoneId, modalidad = "Urgente",
            bultos = new[] { new { pesoGramos = 1m, largoCentimetros = 1m, anchoCentimetros = 1m, altoCentimetros = 1m } }
        });
        Assert.Equal(HttpStatusCode.Conflict, inactiveZoneSimulation.StatusCode);

        using var history = await client.GetAsync("/api/configuracion/tarifas/publicaciones?pagina=1&tamanoPagina=1");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        var historyPage = await history.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, historyPage.GetProperty("total").GetInt32());
        Assert.Equal(draftId, historyPage.GetProperty("items")[0].GetProperty("versionTarifarioId").GetGuid());
        using var historicalDetail = await client.GetAsync($"/api/configuracion/tarifas/publicaciones/{draftId}");
        Assert.Equal(HttpStatusCode.OK, historicalDetail.StatusCode);
        using var noUrgentRate = await client.PostAsJsonAsync("/api/configuracion/tarifas/simulaciones", new
        {
            versionTarifarioId = draftId, zonaId = zoneId, modalidad = "Urgente",
            bultos = new[] { new { pesoGramos = 1m, largoCentimetros = 1m, anchoCentimetros = 1m, altoCentimetros = 1m } }
        });
        Assert.Equal(HttpStatusCode.NotFound, noUrgentRate.StatusCode);

        using var nextDraftResponse = await client.PostAsync("/api/configuracion/tarifas/borrador", null);
        Assert.Equal(HttpStatusCode.Created, nextDraftResponse.StatusCode);
        var nextDraft = await nextDraftResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, nextDraft.GetProperty("versionConcurrencia").GetInt64());
        Assert.Equal(2, nextDraft.GetProperty("tarifas").GetArrayLength());
        using var updatedToZero = await client.PutAsJsonAsync("/api/configuracion/tarifas/borrador/tarifas", new
        {
            versionConcurrencia = 0,
            tarifas = new[]
            {
                new { zonaId = zoneId, modalidad = "Estandar", importeBaseUyu = 0m, precioPorKilogramoUyu = 0m,
                    precioPorMetroCubicoUyu = 0m, ajustes = Array.Empty<object>() }
            }
        });
        Assert.Equal(HttpStatusCode.OK, updatedToZero.StatusCode);
        using var oldPublicationAfterDraftEdit = await client.GetAsync($"/api/configuracion/tarifas/publicaciones/{draftId}");
        var unchangedPublication = await oldPublicationAfterDraftEdit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(120m, unchangedPublication.GetProperty("tarifas")[0].GetProperty("importeBaseUyu").GetDecimal());
        simulation = new
        {
            versionTarifarioId = nextDraft.GetProperty("versionTarifarioId").GetGuid(),
            zonaId = zoneId,
            modalidad = "Estandar",
            bultos = new[] { new { pesoGramos = 1m, largoCentimetros = 1m, anchoCentimetros = 1m, altoCentimetros = 1m } }
        };
        using var zeroSimulation = await client.PostAsJsonAsync("/api/configuracion/tarifas/simulaciones", simulation);
        Assert.Equal(HttpStatusCode.OK, zeroSimulation.StatusCode);
        var zeroResult = await zeroSimulation.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0m, zeroResult.GetProperty("totalFinalUyu").GetDecimal());

        using var secondPublicationResponse = await client.PostAsJsonAsync("/api/configuracion/tarifas/borrador/publicar",
            new { versionConcurrencia = 1 });
        Assert.Equal(HttpStatusCode.OK, secondPublicationResponse.StatusCode);
        var secondPublication = await secondPublicationResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, secondPublication.GetProperty("numeroVersionPublicada").GetInt32());
        using var firstHistoryPageResponse = await client.GetAsync("/api/configuracion/tarifas/publicaciones?pagina=1&tamanoPagina=1");
        var firstHistoryPage = await firstHistoryPageResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, firstHistoryPage.GetProperty("total").GetInt32());
        Assert.Equal(2, firstHistoryPage.GetProperty("items")[0].GetProperty("numeroVersionPublicada").GetInt32());
        using var secondHistoryPageResponse = await client.GetAsync("/api/configuracion/tarifas/publicaciones?pagina=2&tamanoPagina=1");
        var secondHistoryPage = await secondHistoryPageResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, secondHistoryPage.GetProperty("items")[0].GetProperty("numeroVersionPublicada").GetInt32());

        using var absentPublication = await client.GetAsync($"/api/configuracion/tarifas/publicaciones/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, absentPublication.StatusCode);
        using var invalidEnum = await client.PostAsJsonAsync("/api/configuracion/tarifas/simulaciones", new
        {
            versionTarifarioId = draftId, zonaId = zoneId, modalidad = "Express",
            bultos = new[] { new { pesoGramos = 1m, largoCentimetros = 1m, anchoCentimetros = 1m, altoCentimetros = 1m } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidEnum.StatusCode);
    }

    [Fact]
    public async Task OperatorWithoutTarifarioGetsAnEmptyStatusThroughTheRestrictedApiRole()
    {
        var database = await postgres.CreateDatabaseAsync();
        string operatorEmail;
        using (var bootstrapFactory = new IntegrationApiFactory(database))
        using (bootstrapFactory.CreateHttpClient())
        {
            await using var db = database.CreateDbContext();
            operatorEmail = await db.AccesosUsuario.Where(x => x.OperadorId != null)
                .Join(db.Usuarios, access => access.UsuarioId, user => user.Id, (_, user) => user.Email)
                .FirstAsync();
        }

        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = await CreateRestrictedApiConnectionAsync(database),
            ["DatabaseInitialization:ApplyMigrations"] = "false",
            ["DatabaseInitialization:SeedData"] = "false"
        };
        using var factory = new IntegrationApiFactory(database, settings: settings, replaceDbContext: false);
        using var client = factory.CreateHttpClient();
        await LoginAsync(client, operatorEmail);

        using var response = await client.GetAsync("/api/configuracion/tarifas");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var status = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(status.RootElement.GetProperty("configurado").GetBoolean());
        Assert.Equal(JsonValueKind.Null, status.RootElement.GetProperty("publicacionVigente").ValueKind);
        Assert.Equal(JsonValueKind.Null, status.RootElement.GetProperty("borrador").ValueKind);
    }

    [Fact]
    public async Task TarifarioEndpointsKeepExistingAuthenticationAndOperatorPermissionBoundary()
    {
        var database = await postgres.CreateDatabaseAsync();
        string operatorEmail;
        string commerceEmail;
        string noPermissionEmail;
        string platformEmail;
        using (var bootstrapFactory = new IntegrationApiFactory(database))
        using (bootstrapFactory.CreateHttpClient())
        {
            await using var db = database.CreateDbContext();
            var operatorAccess = await db.AccesosUsuario.Where(x => x.OperadorId != null).FirstAsync();
            operatorEmail = await db.Usuarios.Where(x => x.Id == operatorAccess.UsuarioId).Select(x => x.Email).SingleAsync();
            var operatorId = operatorAccess.OperadorId!.Value;
            var commerceAccess = await db.AccesosUsuario.FirstAsync(x => x.ComercioId != null);
            commerceEmail = await db.Usuarios.Where(x => x.Id == commerceAccess.UsuarioId).Select(x => x.Email).SingleAsync();
            var commerceRole = await db.Roles.SingleAsync(x => x.Id == commerceAccess.RolId);
            var permission = await db.Permisos.SingleAsync(x => x.Codigo == "configuracion.gestionar");
            if (!await db.RolesPermisos.AnyAsync(x => x.RolId == commerceRole.Id && x.PermisoId == permission.Id))
                db.RolesPermisos.Add(new RolPermiso(commerceRole.Id, permission.Id));
            var operatorRole = await db.Roles.SingleAsync(x => x.OperadorId == operatorId && x.Codigo == "usuario_operador");
            noPermissionEmail = $"no-tarifario-{Guid.NewGuid():N}@example.test";
            var noPermission = new Usuario(noPermissionEmail, AuthenticationTestData.Hash(noPermissionEmail));
            db.AddRange(noPermission, new AccesoUsuario(noPermission.Id, operatorId, null, operatorRole));
            platformEmail = $"platform-tarifas-{Guid.NewGuid():N}@example.test";
            await AuthenticationTestData.PlatformRepository(db).InicializarAsync(platformEmail, AuthenticationTestData.Password);
            await db.SaveChangesAsync();
        }

        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = await CreateRestrictedApiConnectionAsync(database),
            ["DatabaseInitialization:ApplyMigrations"] = "false",
            ["DatabaseInitialization:SeedData"] = "false"
        };
        using var factory = new IntegrationApiFactory(database, settings: settings, replaceDbContext: false);
        using var anonymous = factory.CreateHttpClient();
        using var unauthorized = await anonymous.GetAsync("/api/configuracion/tarifas");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var operatorWithoutPermission = factory.CreateHttpClient();
        await LoginAsync(operatorWithoutPermission, noPermissionEmail);
        using var denied = await operatorWithoutPermission.GetAsync("/api/configuracion/tarifas");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        using var commerce = factory.CreateHttpClient();
        await LoginAsync(commerce, commerceEmail);
        using var commerceDenied = await commerce.GetAsync("/api/configuracion/tarifas");
        Assert.Equal(HttpStatusCode.Forbidden, commerceDenied.StatusCode);

        using var platform = factory.CreateHttpClient();
        await LoginAsync(platform, platformEmail);
        using var platformDenied = await platform.GetAsync("/api/configuracion/tarifas");
        Assert.Equal(HttpStatusCode.Forbidden, platformDenied.StatusCode);

        using var identityOnly = factory.CreateHttpClient();
        var identityEmail = $"identity-only-{Guid.NewGuid():N}@example.test";
        using var registered = await identityOnly.PostAsJsonAsync("/api/auth/register",
            new { email = identityEmail, password = AuthenticationTestData.Password });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        await LoginAsync(identityOnly, identityEmail);
        using var noOrganization = await identityOnly.GetAsync("/api/configuracion/tarifas");
        Assert.Equal(HttpStatusCode.Forbidden, noOrganization.StatusCode);

        using var operatorClient = factory.CreateHttpClient();
        await LoginAsync(operatorClient, operatorEmail);
        using var allowed = await operatorClient.GetAsync("/api/configuracion/tarifas");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task PublicationIdentifiersFromAnotherOperatorAreNotVisibleThroughHttp()
    {
        var database = await postgres.CreateDatabaseAsync();
        string operatorAEmail;
        using (var bootstrapFactory = new IntegrationApiFactory(database))
        using (bootstrapFactory.CreateHttpClient())
        {
            await using var db = database.CreateDbContext();
            operatorAEmail = await db.AccesosUsuario.Where(x => x.OperadorId != null)
                .Join(db.Usuarios, access => access.UsuarioId, user => user.Id, (_, user) => user.Email)
                .FirstAsync();
        }

        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = await CreateRestrictedApiConnectionAsync(database),
            ["DatabaseInitialization:ApplyMigrations"] = "false",
            ["DatabaseInitialization:SeedData"] = "false"
        };
        using var factory = new IntegrationApiFactory(database, settings: settings, replaceDbContext: false);
        using var client = factory.CreateHttpClient();
        await LoginAsync(client, operatorAEmail);
        var operatorBEmail = $"tariff-operator-b-{Guid.NewGuid():N}@example.test";
        using var registration = await client.PostAsJsonAsync("/api/auth/register",
            new { email = operatorBEmail, password = AuthenticationTestData.Password });
        Assert.Equal(HttpStatusCode.Created, registration.StatusCode);
        await LoginAsync(client, operatorBEmail);
        using var organization = await client.PostAsJsonAsync("/api/alta-propia/operadores", new { nombre = "Tariff operator B" });
        Assert.Equal(HttpStatusCode.Created, organization.StatusCode);
        await LoginAsync(client, operatorBEmail);

        Guid operatorBId;
        Guid zoneBId;
        await using (var db = database.CreateDbContext())
        {
            operatorBId = await db.AccesosUsuario.Where(x => x.OperadorId != null)
                .Join(db.Usuarios, access => access.UsuarioId, user => user.Id,
                    (access, user) => new { access.OperadorId, user.Email })
                .Where(x => x.Email == operatorBEmail).Select(x => x.OperadorId!.Value).SingleAsync();
            var geometryFactory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
            var geometry = geometryFactory.CreateMultiPolygon([geometryFactory.CreatePolygon(
                [new(-56, -34), new(-55, -34), new(-55, -33), new(-56, -33), new(-56, -34)])]);
            var zone = ZonaCobertura.Crear(operatorBId, "Private zone", geometry,
                franjasIniciales: [new(DiaSemanaIso.Lunes, new TimeOnly(9, 0), new TimeOnly(10, 0))]);
            zone.Activar(zone.Version);
            db.ZonasCobertura.Add(zone);
            await db.SaveChangesAsync();
            zoneBId = zone.Id;
        }

        using var draftResponse = await client.PostAsync("/api/configuracion/tarifas/borrador", null);
        Assert.Equal(HttpStatusCode.Created, draftResponse.StatusCode);
        var draft = await draftResponse.Content.ReadFromJsonAsync<JsonElement>();
        var versionBId = draft.GetProperty("versionTarifarioId").GetGuid();
        using var rates = await client.PutAsJsonAsync("/api/configuracion/tarifas/borrador/tarifas", new
        {
            versionConcurrencia = 0,
            tarifas = new[]
            {
                new { zonaId = zoneBId, modalidad = "Estandar", importeBaseUyu = 10m,
                    precioPorKilogramoUyu = 0m, precioPorMetroCubicoUyu = 0m, ajustes = Array.Empty<object>() }
            }
        });
        Assert.Equal(HttpStatusCode.OK, rates.StatusCode);
        using var publication = await client.PostAsJsonAsync("/api/configuracion/tarifas/borrador/publicar",
            new { versionConcurrencia = 1 });
        Assert.Equal(HttpStatusCode.OK, publication.StatusCode);

        await LoginAsync(client, operatorAEmail);
        using var foreignPublication = await client.GetAsync($"/api/configuracion/tarifas/publicaciones/{versionBId}");
        Assert.Equal(HttpStatusCode.NotFound, foreignPublication.StatusCode);
        using var foreignSimulation = await client.PostAsJsonAsync("/api/configuracion/tarifas/simulaciones", new
        {
            versionTarifarioId = versionBId, zonaId = zoneBId, modalidad = "Estandar",
            bultos = new[] { new { pesoGramos = 1m, largoCentimetros = 1m, anchoCentimetros = 1m, altoCentimetros = 1m } }
        });
        Assert.Equal(HttpStatusCode.NotFound, foreignSimulation.StatusCode);
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
        var role = $"cu03_http_{Guid.NewGuid():N}";
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
