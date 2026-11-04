using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NetTopologySuite;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.ZonasCobertura;
using NexoRuta.Infrastructure.Administracion.Repositories;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;
using Npgsql;
using StackExchange.Redis;

namespace NexoRuta.IntegrationTests.Tarifas;

public sealed class TarifarioHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task PublishedTariffSnapshotIsSharedAcrossHostsAndSimulationRechecksLiveZoneState()
    {
        var database = await postgres.CreateDatabaseAsync();
        Guid operatorAccessId;
        Guid operatorId;
        Guid zoneId;
        using (var bootstrap = new IntegrationApiFactory(database))
        using (bootstrap.CreateHttpClient())
        {
            await using var db = database.CreateDbContext();
            var access = await db.AccesosUsuario.FirstAsync(item => item.OperadorId != null);
            operatorAccessId = access.Id;
            operatorId = access.OperadorId!.Value;
            var geometryFactory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
            var geometry = geometryFactory.CreateMultiPolygon([geometryFactory.CreatePolygon(
                [new(-56, -34), new(-55, -34), new(-55, -33), new(-56, -33), new(-56, -34)])]);
            var zone = ZonaCobertura.Crear(operatorId, "Shared cache zone", geometry,
                franjasIniciales: [new(DiaSemanaIso.Lunes, new TimeOnly(9, 0), new TimeOnly(10, 0))]);
            zone.Activar(zone.Version);
            db.ZonasCobertura.Add(zone);
            await db.SaveChangesAsync();
            zoneId = zone.Id;
        }

        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(database);
        await using var redis = await RedisTestServer.CreateAsync();
        var namespaceValue = $"integration-{Guid.NewGuid():N}";
        using var redisConnection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var settings = RlsRestrictedHttpFlowTests.ApiSettings();
        settings["ConnectionStrings:Redis"] = redis.ConnectionString;
        settings["Caching:Enabled"] = "true";
        settings["Caching:Namespace"] = namespaceValue;
        settings["Caching:PublishedTariff:TtlSeconds"] = "60";
        var graphQueriesA = new TariffGraphQueryCounter();
        var graphQueriesB = new TariffGraphQueryCounter();
        using var apiA = new IntegrationApiFactory(runtime, settings: settings, interceptors: [graphQueriesA]);
        using var apiB = new IntegrationApiFactory(runtime, settings: settings, interceptors: [graphQueriesB]);
        using var clientA = apiA.CreateHttpClient();
        using var clientB = apiB.CreateHttpClient();
        await clientA.SetBearerForAccessAsync(database, operatorAccessId);
        await clientB.SetBearerForAccessAsync(database, operatorAccessId);

        using var createdDraft = await clientA.PostAsync("/api/configuracion/tarifas/borrador", null);
        Assert.Equal(HttpStatusCode.Created, createdDraft.StatusCode);
        var draftId = (await createdDraft.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("versionTarifarioId").GetGuid();
        using var configuredDraft = await clientA.PutAsJsonAsync("/api/configuracion/tarifas/borrador/tarifas", new
        {
            versionConcurrencia = 0,
            tarifas = new[]
            {
                new
                {
                    zonaId = zoneId,
                    modalidad = "Estandar",
                    importeBaseUyu = 100m,
                    precioPorKilogramoUyu = 10m,
                    precioPorMetroCubicoUyu = 0m,
                    ajustes = new[]
                    {
                        new { nombre = "Handling", tipo = "RecargoFijo", importeFijoUyu = (decimal?)5m, porcentaje = (decimal?)null }
                    }
                }
            }
        });
        Assert.Equal(HttpStatusCode.OK, configuredDraft.StatusCode);

        graphQueriesB.Reset();
        using (var draftSimulation = await SimulateAsync(clientB, draftId, zoneId, 1000m))
        {
            Assert.Equal(HttpStatusCode.OK, draftSimulation.StatusCode);
            var result = await draftSimulation.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Borrador", result.GetProperty("origen").GetString());
            Assert.Equal(115m, result.GetProperty("totalFinalUyu").GetDecimal());
        }
        Assert.True(graphQueriesB.Count > 0);

        using var editedDraft = await clientA.PutAsJsonAsync("/api/configuracion/tarifas/borrador/tarifas", new
        {
            versionConcurrencia = 1,
            tarifas = new[]
            {
                new { zonaId = zoneId, modalidad = "Estandar", importeBaseUyu = 200m,
                    precioPorKilogramoUyu = 20m, precioPorMetroCubicoUyu = 0m,
                    ajustes = new[]
                    {
                        new { nombre = "Handling", tipo = "RecargoFijo", importeFijoUyu = (decimal?)5m, porcentaje = (decimal?)null }
                    } }
            }
        });
        Assert.Equal(HttpStatusCode.OK, editedDraft.StatusCode);
        Assert.Equal(2L, (await editedDraft.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("versionConcurrencia").GetInt64());

        graphQueriesB.Reset();
        using (var editedDraftSimulation = await SimulateAsync(clientB, draftId, zoneId, 1000m))
        {
            Assert.Equal(HttpStatusCode.OK, editedDraftSimulation.StatusCode);
            var result = await editedDraftSimulation.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Borrador", result.GetProperty("origen").GetString());
            Assert.Equal(225m, result.GetProperty("totalFinalUyu").GetDecimal());
        }
        Assert.True(graphQueriesB.Count > 0);

        using var published = await clientA.PostAsJsonAsync("/api/configuracion/tarifas/borrador/publicar",
            new { versionConcurrencia = 2 });
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        Assert.Equal(draftId, (await published.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("versionTarifarioId").GetGuid());

        graphQueriesA.Reset();
        using (var detail = await clientA.GetAsync($"/api/configuracion/tarifas/publicaciones/{draftId}"))
        {
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            var publication = await detail.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(200m, publication.GetProperty("tarifas")[0].GetProperty("importeBaseUyu").GetDecimal());
        }
        Assert.True(graphQueriesA.Count > 0);

        graphQueriesB.Reset();
        using (var simulation = await SimulateAsync(clientB, draftId, zoneId, 1000m))
        {
            Assert.Equal(HttpStatusCode.OK, simulation.StatusCode);
            Assert.Equal(225m, (await simulation.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("totalFinalUyu").GetDecimal());
        }
        using (var secondSimulation = await SimulateAsync(clientB, draftId, zoneId, 2000m))
        {
            Assert.Equal(HttpStatusCode.OK, secondSimulation.StatusCode);
            Assert.Equal(245m, (await secondSimulation.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("totalFinalUyu").GetDecimal());
        }
        Assert.Equal(0, graphQueriesB.Count);

        await using (var owner = database.CreateDbContext())
        {
            var zone = await owner.ZonasCobertura.Include(item => item.Franjas)
                .SingleAsync(item => item.Id == zoneId);
            zone.Desactivar(zone.Version);
            await owner.SaveChangesAsync();
        }
        using var inactive = await SimulateAsync(clientB, draftId, zoneId, 1000m);
        Assert.Equal(HttpStatusCode.Conflict, inactive.StatusCode);
        Assert.Equal(0, graphQueriesB.Count);

        await using (var owner = database.CreateDbContext())
        {
            var zone = await owner.ZonasCobertura.Include(item => item.Franjas)
                .SingleAsync(item => item.Id == zoneId);
            zone.Activar(zone.Version);
            await owner.SaveChangesAsync();
        }
        graphQueriesB.Reset();
        using (var recovered = await SimulateAsync(clientB, draftId, zoneId, 1000m))
        {
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
            Assert.Equal(225m, (await recovered.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("totalFinalUyu").GetDecimal());
        }
        Assert.Equal(0, graphQueriesB.Count);

        using var nextDraftResponse = await clientA.PostAsync("/api/configuracion/tarifas/borrador", null);
        Assert.Equal(HttpStatusCode.Created, nextDraftResponse.StatusCode);
        var nextDraft = await nextDraftResponse.Content.ReadFromJsonAsync<JsonElement>();
        var nextVersionId = nextDraft.GetProperty("versionTarifarioId").GetGuid();
        using var nextDraftRates = await clientA.PutAsJsonAsync("/api/configuracion/tarifas/borrador/tarifas", new
        {
            versionConcurrencia = 0,
            tarifas = new[]
            {
                new { zonaId = zoneId, modalidad = "Estandar", importeBaseUyu = 300m,
                    precioPorKilogramoUyu = 0m, precioPorMetroCubicoUyu = 0m,
                    ajustes = new[]
                    {
                        new { nombre = "Handling", tipo = "RecargoFijo", importeFijoUyu = (decimal?)10m, porcentaje = (decimal?)null }
                    } }
            }
        });
        Assert.Equal(HttpStatusCode.OK, nextDraftRates.StatusCode);

        await InstallTariffCommitFailureAsync(database);
        HttpStatusCode? failedPublicationStatus = null;
        Exception? failedPublication = null;
        try
        {
            using var response = await clientA.PostAsJsonAsync("/api/configuracion/tarifas/borrador/publicar",
                new { versionConcurrencia = 1 });
            failedPublicationStatus = response.StatusCode;
        }
        catch (Exception exception)
        {
            failedPublication = exception;
        }
        if (failedPublication is null)
            Assert.Equal(HttpStatusCode.InternalServerError, failedPublicationStatus);
        else
            Assert.IsType<PostgresException>(failedPublication.GetBaseException());

        using (var unchangedState = await clientA.GetAsync("/api/configuracion/tarifas"))
        {
            Assert.Equal(HttpStatusCode.OK, unchangedState.StatusCode);
            var current = await unchangedState.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(draftId, current.GetProperty("publicacionVigente")
                .GetProperty("versionTarifarioId").GetGuid());
            Assert.Equal(nextVersionId, current.GetProperty("borrador")
                .GetProperty("versionTarifarioId").GetGuid());
        }
        var abortedCacheKey = $"nexoruta:{namespaceValue}:cache:v1:published_tariff:{operatorId:D}:{nextVersionId:D}";
        Assert.False(await redisConnection.GetDatabase().KeyExistsAsync(abortedCacheKey));
        await RemoveTariffCommitFailureAsync(database);

        using var nextPublication = await clientA.PostAsJsonAsync("/api/configuracion/tarifas/borrador/publicar",
            new { versionConcurrencia = 1 });
        Assert.Equal(HttpStatusCode.OK, nextPublication.StatusCode);

        graphQueriesA.Reset();
        var readGate = graphQueriesA.PauseNextGraphRead();
        var delayedDetailTask = clientA.GetAsync($"/api/configuracion/tarifas/publicaciones/{nextVersionId}");
        Guid latestVersionId;
        try
        {
            await readGate.WaitUntilPausedAsync(TimeSpan.FromSeconds(20));
            using var newerDraftResponse = await clientB.PostAsync("/api/configuracion/tarifas/borrador", null);
            Assert.Equal(HttpStatusCode.Created, newerDraftResponse.StatusCode);
            latestVersionId = (await newerDraftResponse.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("versionTarifarioId").GetGuid();
            using var newerDraftRates = await clientB.PutAsJsonAsync("/api/configuracion/tarifas/borrador/tarifas", new
            {
                versionConcurrencia = 0,
                tarifas = new[]
                {
                    new { zonaId = zoneId, modalidad = "Estandar", importeBaseUyu = 400m,
                        precioPorKilogramoUyu = 0m, precioPorMetroCubicoUyu = 0m,
                        ajustes = new[]
                        {
                            new { nombre = "Handling", tipo = "RecargoFijo", importeFijoUyu = (decimal?)10m,
                                porcentaje = (decimal?)null }
                        } }
                }
            });
            Assert.Equal(HttpStatusCode.OK, newerDraftRates.StatusCode);
            using var newerPublication = await clientB.PostAsJsonAsync(
                "/api/configuracion/tarifas/borrador/publicar", new { versionConcurrencia = 1 });
            Assert.Equal(HttpStatusCode.OK, newerPublication.StatusCode);
            Assert.Equal(latestVersionId, (await newerPublication.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("versionTarifarioId").GetGuid());
        }
        finally
        {
            readGate.Release();
        }

        using (var delayedDetail = await delayedDetailTask)
        {
            Assert.Equal(HttpStatusCode.OK, delayedDetail.StatusCode);
            var delayedSnapshot = await delayedDetail.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(2, delayedSnapshot.GetProperty("numeroVersionPublicada").GetInt32());
            Assert.Equal(300m, delayedSnapshot.GetProperty("tarifas")[0].GetProperty("importeBaseUyu").GetDecimal());
        }
        Assert.True(graphQueriesA.Count > 0);

        using (var status = await clientA.GetAsync("/api/configuracion/tarifas"))
        {
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
            var current = await status.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(latestVersionId, current.GetProperty("publicacionVigente")
                .GetProperty("versionTarifarioId").GetGuid());
        }
        graphQueriesA.Reset();
        using (var latestDetail = await clientA.GetAsync($"/api/configuracion/tarifas/publicaciones/{latestVersionId}"))
        {
            Assert.Equal(HttpStatusCode.OK, latestDetail.StatusCode);
            var latestSnapshot = await latestDetail.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(3, latestSnapshot.GetProperty("numeroVersionPublicada").GetInt32());
            Assert.Equal(400m, latestSnapshot.GetProperty("tarifas")[0].GetProperty("importeBaseUyu").GetDecimal());
        }
        Assert.True(graphQueriesA.Count > 0);
        graphQueriesB.Reset();
        using (var latestSimulation = await SimulateAsync(clientB, latestVersionId, zoneId, 1000m))
        {
            Assert.Equal(HttpStatusCode.OK, latestSimulation.StatusCode);
            Assert.Equal(410m, (await latestSimulation.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("totalFinalUyu").GetDecimal());
        }
        Assert.Equal(0, graphQueriesB.Count);

        graphQueriesB.Reset();
        using (var nextSimulation = await SimulateAsync(clientB, nextVersionId, zoneId, 1000m))
        {
            Assert.Equal(HttpStatusCode.OK, nextSimulation.StatusCode);
            Assert.Equal(310m, (await nextSimulation.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("totalFinalUyu").GetDecimal());
        }
        Assert.Equal(0, graphQueriesB.Count);

        using var historical = await clientB.GetAsync($"/api/configuracion/tarifas/publicaciones/{draftId}");
        Assert.Equal(HttpStatusCode.OK, historical.StatusCode);
        var historicalSnapshot = await historical.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, historicalSnapshot.GetProperty("numeroVersionPublicada").GetInt32());
        Assert.Equal(200m, historicalSnapshot.GetProperty("tarifas")[0]
            .GetProperty("importeBaseUyu").GetDecimal());
    }

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
        Assert.Equal(4, configured.GetProperty("tarifas").EnumerateArray()
            .Single(t => t.GetProperty("zonaId").GetGuid() == zoneId).GetProperty("ajustes").GetArrayLength());

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
        Assert.Equal(120m, unchangedPublication.GetProperty("tarifas").EnumerateArray()
            .Single(t => t.GetProperty("zonaId").GetGuid() == zoneId).GetProperty("importeBaseUyu").GetDecimal());
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

    private static Task<HttpResponseMessage> SimulateAsync(HttpClient client, Guid versionId, Guid zoneId,
        decimal weightGrams)
        => client.PostAsJsonAsync("/api/configuracion/tarifas/simulaciones", new
        {
            versionTarifarioId = versionId,
            zonaId = zoneId,
            modalidad = "Estandar",
            bultos = new[] { new { pesoGramos = weightGrams, largoCentimetros = 10m,
                anchoCentimetros = 10m, altoCentimetros = 10m } }
        });

    private sealed class TariffGraphQueryCounter : DbCommandInterceptor
    {
        private int count;
        private GraphReadGate? pendingGate;
        public int Count => Volatile.Read(ref count);
        public void Reset() => Interlocked.Exchange(ref count, 0);

        public GraphReadGate PauseNextGraphRead()
        {
            var gate = new GraphReadGate();
            if (Interlocked.CompareExchange(ref pendingGate, gate, null) is not null)
                throw new InvalidOperationException("A tariff graph query gate is already armed.");
            return gate;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("TarifasZonaModalidad", StringComparison.Ordinal)
                || command.CommandText.Contains("AjustesTarifario", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref count);
                var gate = Interlocked.Exchange(ref pendingGate, null);
                if (gate is not null)
                {
                    gate.Pause();
                    await gate.WaitForReleaseAsync(cancellationToken);
                }
            }
            return result;
        }

        public sealed class GraphReadGate
        {
            private readonly TaskCompletionSource<bool> paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> released = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task WaitUntilPausedAsync(TimeSpan timeout) => paused.Task.WaitAsync(timeout);
            public void Release() => released.TrySetResult(true);
            internal void Pause() => paused.TrySetResult(true);
            internal Task WaitForReleaseAsync(CancellationToken cancellationToken) => released.Task.WaitAsync(cancellationToken);
        }
    }

    private static async Task InstallTariffCommitFailureAsync(IntegrationDatabase database)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var function = new NpgsqlCommand("""
            CREATE OR REPLACE FUNCTION public.nfr07_reject_tariff_commit() RETURNS trigger
            LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'nfr07 injected transaction rollback'; END $$;
            """, connection))
            await function.ExecuteNonQueryAsync();
        await using var trigger = new NpgsqlCommand("""
            CREATE CONSTRAINT TRIGGER nfr07_reject_tariff_publication
            AFTER UPDATE ON public."TarifariosOperador"
            DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW EXECUTE FUNCTION public.nfr07_reject_tariff_commit();
            """, connection);
        await trigger.ExecuteNonQueryAsync();
    }

    private static async Task RemoveTariffCommitFailureAsync(IntegrationDatabase database)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var trigger = new NpgsqlCommand(
            "DROP TRIGGER IF EXISTS nfr07_reject_tariff_publication ON public.\"TarifariosOperador\"", connection))
            await trigger.ExecuteNonQueryAsync();
        await using var function = new NpgsqlCommand(
            "DROP FUNCTION IF EXISTS public.nfr07_reject_tariff_commit()", connection);
        await function.ExecuteNonQueryAsync();
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "backend", "src", "NexoRuta.Api", "NexoRuta.Api.csproj")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate the NexoRuta repository root from the test output directory.");
    }
}
