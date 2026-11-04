using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using System.Data.Common;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NexoRuta.Domain.Administracion;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;
using Npgsql;
using StackExchange.Redis;

namespace NexoRuta.IntegrationTests.Administracion;

public sealed class IdentidadVisualOperadorHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task IdentitySnapshotIsSharedAcrossApiHostsAndNewVersionIsVisibleAfterCommit()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var bootstrapApi = database.CreateApiFactory();
        using var seedClient = bootstrapApi.CreateHttpClient();
        var actors = await SeedActorsAsync(database);
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(database);
        await using var redis = await RedisTestServer.CreateAsync();

        var namespaceValue = $"integration-{Guid.NewGuid():N}";
        var settings = RlsRestrictedHttpFlowTests.ApiSettings();
        settings["ConnectionStrings:Redis"] = redis.ConnectionString;
        settings["Caching:Enabled"] = "true";
        settings["Caching:Namespace"] = namespaceValue;
        settings["Caching:OperatorBranding:TtlSeconds"] = "60";
        settings["Caching:PublishedTariff:TtlSeconds"] = "60";
        var queriesA = new IdentitySnapshotQueryCounter();
        var queriesB = new IdentitySnapshotQueryCounter();
        var cacheMetrics = new ConcurrentBag<CacheMetric>();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "NexoRuta.Hosting"
                && instrument.Name == "nexoruta.cache.distributed.requests")
                listener.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            cacheMetrics.Add(new(instrument.Name, value, tags.ToArray())));
        meterListener.Start();
        using var apiA = new IntegrationApiFactory(runtime, settings: settings, interceptors: [queriesA]);
        using var apiB = new IntegrationApiFactory(runtime, settings: settings, interceptors: [queriesB]);
        using var clientA = apiA.CreateHttpClient();
        using var clientB = apiB.CreateHttpClient();
        await clientA.SetBearerForAccessAsync(database, actors.OperatorEmployeeA.AccessId);
        await clientB.SetBearerForAccessAsync(database, actors.OperatorEmployeeA.AccessId);

        using (var saved = await clientA.PutAsync("/api/configuracion/identidad-visual", Form(0, "Marca compartida v1")))
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        queriesA.Reset();
        using (var firstRead = await clientA.GetAsync("/api/configuracion/identidad-visual"))
        {
            Assert.Equal(HttpStatusCode.OK, firstRead.StatusCode);
            Assert.Equal("Marca compartida v1",
                (await firstRead.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("nombreComercial").GetString());
        }
        Assert.Equal(1, queriesA.SnapshotReads);

        await clientB.SetBearerForAccessAsync(database, actors.Commerce.AccessId);
        queriesB.Reset();
        using (var sharedRead = await clientB.GetAsync(
            $"/api/operadores/{actors.OperatorAId:D}/identidad-visual"))
        {
            Assert.Equal(HttpStatusCode.OK, sharedRead.StatusCode);
            Assert.Equal("Marca compartida v1",
                (await sharedRead.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("nombre").GetString());
        }
        Assert.Equal(0, queriesB.SnapshotReads);

        using (var saved = await clientA.PutAsync("/api/configuracion/identidad-visual", Form(1, "Marca compartida v2")))
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        queriesB.Reset();
        using (var refreshed = await clientB.GetAsync(
            $"/api/operadores/{actors.OperatorAId:D}/identidad-visual"))
        {
            Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
            var snapshot = await refreshed.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("Marca compartida v2", snapshot.GetProperty("nombre").GetString());
        }
        Assert.Equal(1, queriesB.SnapshotReads);

        queriesA.Reset();
        using (var refreshedFromA = await clientA.GetAsync("/api/configuracion/identidad-visual"))
        {
            Assert.Equal(HttpStatusCode.OK, refreshedFromA.StatusCode);
            Assert.Equal(2, (await refreshedFromA.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("version").GetInt32());
        }
        Assert.Equal(0, queriesA.SnapshotReads);

        using var redisConnection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var cacheKey = $"nexoruta:{namespaceValue}:cache:v1:operator_branding:{actors.OperatorAId:D}:2";
        await apiB.Services.GetRequiredService<IDistributedCache>().SetAsync(cacheKey,
            Encoding.UTF8.GetBytes("{not-json"), new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2)
            });
        queriesB.Reset();
        using (var corruptRead = await clientB.GetAsync(
            $"/api/operadores/{actors.OperatorAId:D}/identidad-visual"))
        {
            Assert.Equal(HttpStatusCode.OK, corruptRead.StatusCode);
            Assert.Equal("Marca compartida v2",
                (await corruptRead.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("nombre").GetString());
        }
        Assert.Equal(1, queriesB.SnapshotReads);

        queriesB.Reset();
        using (var recoveredRead = await clientB.GetAsync(
            $"/api/operadores/{actors.OperatorAId:D}/identidad-visual"))
        {
            Assert.Equal(HttpStatusCode.OK, recoveredRead.StatusCode);
            Assert.Equal("Marca compartida v2",
                (await recoveredRead.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("nombre").GetString());
        }
        Assert.Equal(0, queriesB.SnapshotReads);

        var identityDecisions = cacheMetrics.Where(sample => sample.Value > 0
            && sample.Instrument == "nexoruta.cache.distributed.requests"
            && sample.Tag("cache") == "operator_branding").ToArray();
        Assert.Equal(6, identityDecisions.Length);
        Assert.Equal(2, identityDecisions.Count(sample => sample.Tag("result") == "miss"));
        Assert.Equal(3, identityDecisions.Count(sample => sample.Tag("result") == "hit"));
        Assert.Single(identityDecisions, sample => sample.Tag("result") == "error");
        Assert.All(identityDecisions, sample => Assert.All(sample.Tags,
            tag => Assert.Contains(tag.Key, new[] { "cache", "result" })));

        await clientB.SetBearerForAccessAsync(database, actors.OperatorB.AccessId);
        using var isolatedRead = await clientB.GetAsync("/api/configuracion/identidad-visual");
        Assert.Equal(HttpStatusCode.OK, isolatedRead.StatusCode);
        var isolatedBrand = await isolatedRead.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, isolatedBrand.GetProperty("version").GetInt32());
        Assert.Equal("Second operator", isolatedBrand.GetProperty("nombreMostrado").GetString());
    }

    [Fact]
    public async Task SlowIdentityReaderOfOldVersionCannotContaminateTheNewVersion()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var bootstrapApi = database.CreateApiFactory();
        using var seedClient = bootstrapApi.CreateHttpClient();
        var actors = await SeedActorsAsync(database);
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(database);
        await using var redis = await RedisTestServer.CreateAsync();
        var namespaceValue = $"integration-{Guid.NewGuid():N}";
        var settings = RlsRestrictedHttpFlowTests.ApiSettings();
        settings["ConnectionStrings:Redis"] = redis.ConnectionString;
        settings["Caching:Enabled"] = "true";
        settings["Caching:Namespace"] = namespaceValue;
        var slowSnapshot = new IdentitySnapshotReadGate();
        var snapshotReadsA = new IdentitySnapshotQueryCounter();
        var snapshotReadsB = new IdentitySnapshotQueryCounter();
        using var apiA = new IntegrationApiFactory(runtime, settings: settings, interceptors: [slowSnapshot, snapshotReadsA]);
        using var apiB = new IntegrationApiFactory(runtime, settings: settings, interceptors: [snapshotReadsB]);
        using var clientA = apiA.CreateHttpClient();
        using var clientB = apiB.CreateHttpClient();
        await clientA.SetBearerForAccessAsync(database, actors.OperatorEmployeeA.AccessId);
        await clientB.SetBearerForAccessAsync(database, actors.OperatorEmployeeA.AccessId);

        using (var saved = await clientA.PutAsync("/api/configuracion/identidad-visual", Form(0, "Marca lenta v1")))
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        snapshotReadsA.Reset();
        slowSnapshot.Arm();

        var delayedReadTask = clientA.GetAsync("/api/configuracion/identidad-visual");
        try
        {
            await slowSnapshot.WaitUntilReachedAsync(TimeSpan.FromSeconds(20));
            using var saved = await clientB.PutAsync("/api/configuracion/identidad-visual", Form(1, "Marca lenta v2"));
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            snapshotReadsB.Reset();
        }
        finally
        {
            slowSnapshot.Release();
        }

        using (var delayedRead = await delayedReadTask)
        {
            Assert.Equal(HttpStatusCode.OK, delayedRead.StatusCode);
            var oldSnapshot = await delayedRead.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(1, oldSnapshot.GetProperty("version").GetInt32());
            Assert.Equal("Marca lenta v1", oldSnapshot.GetProperty("nombreComercial").GetString());
        }
        Assert.Equal(1, snapshotReadsA.SnapshotReads);

        var versionOneKey = $"nexoruta:{namespaceValue}:cache:v1:operator_branding:{actors.OperatorAId:D}:1";
        var versionTwoKey = $"nexoruta:{namespaceValue}:cache:v1:operator_branding:{actors.OperatorAId:D}:2";
        using (var currentRead = await clientB.GetAsync("/api/configuracion/identidad-visual"))
        {
            Assert.Equal(HttpStatusCode.OK, currentRead.StatusCode);
            var currentSnapshot = await currentRead.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(2, currentSnapshot.GetProperty("version").GetInt32());
            Assert.Equal("Marca lenta v2", currentSnapshot.GetProperty("nombreComercial").GetString());
        }
        Assert.Equal(1, snapshotReadsB.SnapshotReads);
        snapshotReadsA.Reset();
        using (var sharedRead = await clientA.GetAsync("/api/configuracion/identidad-visual"))
        {
            Assert.Equal(HttpStatusCode.OK, sharedRead.StatusCode);
            Assert.Equal(2, (await sharedRead.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("version").GetInt32());
        }
        Assert.Equal(0, snapshotReadsA.SnapshotReads);

        using var redisConnection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        Assert.True(await redisConnection.GetDatabase().KeyExistsAsync(versionOneKey));
        Assert.True(await redisConnection.GetDatabase().KeyExistsAsync(versionTwoKey));
    }

    [Fact]
    public async Task RedisOutageAtStartupAndDuringReadsFallsBackAndRecoversWithoutRestartingApi()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var bootstrapApi = database.CreateApiFactory();
        using var seedClient = bootstrapApi.CreateHttpClient();
        var actors = await SeedActorsAsync(database);
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(database);
        await using var redis = await RedisTestServer.CreateRestartableAsync();
        var redisConnectionString = redis.ConnectionString;
        await redis.StopAsync();

        var settings = RlsRestrictedHttpFlowTests.ApiSettings();
        settings["ConnectionStrings:Redis"] = redisConnectionString;
        settings["Caching:Enabled"] = "true";
        settings["Caching:Namespace"] = $"integration-{Guid.NewGuid():N}";
        settings["Caching:OperatorBranding:TtlSeconds"] = "60";
        var queries = new IdentitySnapshotQueryCounter();
        using var api = new IntegrationApiFactory(runtime, settings: settings, interceptors: [queries]);
        using var client = api.CreateHttpClient();
        await client.SetBearerForAccessAsync(database, actors.OperatorEmployeeA.AccessId);
        using (var saved = await client.PutAsync("/api/configuracion/identidad-visual", Form(0, "Resiliencia Redis")))
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        queries.Reset();
        using (var outageRead = await client.GetAsync("/api/configuracion/identidad-visual"))
        {
            Assert.Equal(HttpStatusCode.OK, outageRead.StatusCode);
            Assert.Equal("Resiliencia Redis",
                (await outageRead.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("nombreComercial").GetString());
        }
        Assert.Equal(1, queries.SnapshotReads);

        await redis.StartAsync();
        Assert.Equal(redisConnectionString, redis.ConnectionString);
        await WaitForRedisReadyAsync(redis, redisConnectionString);

        queries.Reset();
        using (var recoveredMiss = await client.GetAsync("/api/configuracion/identidad-visual"))
        {
            Assert.Equal(HttpStatusCode.OK, recoveredMiss.StatusCode);
            Assert.Equal("Resiliencia Redis",
                (await recoveredMiss.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("nombreComercial").GetString());
        }
        Assert.Equal(1, queries.SnapshotReads);
        queries.Reset();
        using (var recoveredHit = await client.GetAsync("/api/configuracion/identidad-visual"))
            Assert.Equal(HttpStatusCode.OK, recoveredHit.StatusCode);
        Assert.Equal(0, queries.SnapshotReads);

        await redis.StopAsync();
        queries.Reset();
        using (var duringOutage = await client.GetAsync("/api/configuracion/identidad-visual"))
        {
            Assert.Equal(HttpStatusCode.OK, duringOutage.StatusCode);
            Assert.Equal("Resiliencia Redis",
                (await duringOutage.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("nombreComercial").GetString());
        }
        Assert.Equal(1, queries.SnapshotReads);

        await redis.StartAsync();
        Assert.Equal(redisConnectionString, redis.ConnectionString);
        await WaitForRedisReadyAsync(redis, redisConnectionString);
        queries.Reset();
        using (var afterRestartMiss = await client.GetAsync("/api/configuracion/identidad-visual"))
            Assert.Equal(HttpStatusCode.OK, afterRestartMiss.StatusCode);
        Assert.Equal(1, queries.SnapshotReads);
        queries.Reset();
        using (var afterRestartHit = await client.GetAsync("/api/configuracion/identidad-visual"))
            Assert.Equal(HttpStatusCode.OK, afterRestartHit.StatusCode);
        Assert.Equal(0, queries.SnapshotReads);
    }

    [Fact]
    public async Task FailedIdentityCommitDoesNotAdvanceOrPublishACacheVersion()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var bootstrapApi = database.CreateApiFactory();
        using var seedClient = bootstrapApi.CreateHttpClient();
        var actors = await SeedActorsAsync(database);
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(database);
        await using var redis = await RedisTestServer.CreateAsync();

        var namespaceValue = $"integration-{Guid.NewGuid():N}";
        using var redisConnection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var settings = RlsRestrictedHttpFlowTests.ApiSettings();
        settings["ConnectionStrings:Redis"] = redis.ConnectionString;
        settings["Caching:Enabled"] = "true";
        settings["Caching:Namespace"] = namespaceValue;
        using var api = new IntegrationApiFactory(runtime, settings: settings);
        using var client = api.CreateHttpClient();
        await client.SetBearerForAccessAsync(database, actors.OperatorEmployeeA.AccessId);

        await InstallIdentityCommitFailureAsync(database);
        Exception? failedWrite = null;
        HttpStatusCode? failedStatus = null;
        try
        {
            using var response = await client.PutAsync("/api/configuracion/identidad-visual",
                Form(0, "No debe quedar publicada"));
            failedStatus = response.StatusCode;
        }
        catch (Exception exception)
        {
            failedWrite = exception;
        }
        if (failedWrite is null)
            Assert.Equal(HttpStatusCode.InternalServerError, failedStatus);
        else
            Assert.True(failedWrite.GetBaseException() is PostgresException { SqlState: "P0001" },
                failedWrite.ToString());

        var rolledBackKey = $"nexoruta:{namespaceValue}:cache:v1:operator_branding:{actors.OperatorAId:D}:1";
        Assert.False(await redisConnection.GetDatabase().KeyExistsAsync(rolledBackKey));
        await RemoveIdentityCommitFailureAsync(database);

        using (var current = await client.GetAsync("/api/configuracion/identidad-visual"))
        {
            Assert.Equal(HttpStatusCode.OK, current.StatusCode);
            var fallback = await current.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(0, fallback.GetProperty("version").GetInt32());
            Assert.Equal("Integration operator", fallback.GetProperty("nombreMostrado").GetString());
        }
        using (var committed = await client.PutAsync("/api/configuracion/identidad-visual",
            Form(0, "Versión confirmada")))
            Assert.Equal(HttpStatusCode.OK, committed.StatusCode);
        Assert.False(await redisConnection.GetDatabase().KeyExistsAsync(rolledBackKey));

        using (var current = await client.GetAsync("/api/configuracion/identidad-visual"))
        {
            Assert.Equal(HttpStatusCode.OK, current.StatusCode);
            var snapshot = await current.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(1, snapshot.GetProperty("version").GetInt32());
            Assert.Equal("Versión confirmada", snapshot.GetProperty("nombreComercial").GetString());
        }
        Assert.True(await redisConnection.GetDatabase().KeyExistsAsync(rolledBackKey));
    }

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

    private static async Task InstallIdentityCommitFailureAsync(IntegrationDatabase database)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var function = new NpgsqlCommand("""
            CREATE OR REPLACE FUNCTION public.nfr07_reject_identity_commit() RETURNS trigger
            LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'nfr07 injected transaction rollback'; END $$;
            """, connection))
            await function.ExecuteNonQueryAsync();
        await using var trigger = new NpgsqlCommand("""
            CREATE CONSTRAINT TRIGGER nfr07_reject_identity_write
            AFTER INSERT ON public."VersionesIdentidadVisualOperador"
            DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW EXECUTE FUNCTION public.nfr07_reject_identity_commit();
            """, connection);
        await trigger.ExecuteNonQueryAsync();
    }

    private static async Task RemoveIdentityCommitFailureAsync(IntegrationDatabase database)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var trigger = new NpgsqlCommand(
            "DROP TRIGGER IF EXISTS nfr07_reject_identity_write ON public.\"VersionesIdentidadVisualOperador\"", connection))
            await trigger.ExecuteNonQueryAsync();
        await using var function = new NpgsqlCommand(
            "DROP FUNCTION IF EXISTS public.nfr07_reject_identity_commit()", connection);
        await function.ExecuteNonQueryAsync();
    }

    private static async Task WaitForRedisReadyAsync(RedisTestServer redis, string connectionString)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        Exception? lastError = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var probe = await ConnectionMultiplexer.ConnectAsync(connectionString);
                if (probe.IsConnected)
                {
                    await probe.GetDatabase().PingAsync();
                    return;
                }
            }
            catch (RedisException exception)
            {
                lastError = exception;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        var diagnostics = await redis.GetRestartDiagnosticsAsync();
        Assert.Fail($"Redis did not accept PING after restarting within 20 seconds. Endpoint={connectionString}. {lastError}{Environment.NewLine}{diagnostics}");
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

    private sealed record CacheMetric(string Instrument, long Value,
        KeyValuePair<string, object?>[] Tags)
    {
        public string? Tag(string name) => Tags.Single(tag => tag.Key == name).Value?.ToString();
    }

    private sealed class IdentitySnapshotQueryCounter : DbCommandInterceptor
    {
        private int snapshotReads;
        public int SnapshotReads => Volatile.Read(ref snapshotReads);
        public void Reset() => Interlocked.Exchange(ref snapshotReads, 0);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("VersionesIdentidadVisualOperador", StringComparison.Ordinal)
                && command.CommandText.Contains("LogoContenido", StringComparison.Ordinal))
                Interlocked.Increment(ref snapshotReads);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class IdentitySnapshotReadGate : DbCommandInterceptor
    {
        private readonly TaskCompletionSource<bool> reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int armed;

        public void Arm() => Interlocked.Exchange(ref armed, 1);

        public Task WaitUntilReachedAsync(TimeSpan timeout)
            => reached.Task.WaitAsync(timeout);

        public void Release() => released.TrySetResult(true);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("VersionesIdentidadVisualOperador", StringComparison.Ordinal)
                && command.CommandText.Contains("LogoContenido", StringComparison.Ordinal)
                && Interlocked.Exchange(ref armed, 0) == 1)
            {
                reached.TrySetResult(true);
                await released.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

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
