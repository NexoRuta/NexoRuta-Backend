using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using NexoRuta.Infrastructure.Caching;
using NexoRuta.Infrastructure.Hosting;
using StackExchange.Redis;

namespace NexoRuta.UnitTests.Caching;

public sealed class DistributedCacheAccessTests
{
    [Fact]
    public void StartupRegistersZeroSamplesForKnownCacheResultsAndOperationsOnce()
    {
        var samples = new ConcurrentBag<MetricSample>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, activeListener) =>
        {
            if (instrument.Meter.Name == "NexoRuta.Hosting"
                && instrument.Name.StartsWith("nexoruta.cache.distributed.", StringComparison.Ordinal))
                activeListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            samples.Add(new(instrument.Name, value, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            samples.Add(new(instrument.Name, value, tags.ToArray())));
        listener.Start();

        DistributedCacheTelemetry.InitializeKnownSeries();
        DistributedCacheTelemetry.InitializeKnownSeries();

        var requests = samples.Where(sample => sample.Instrument == "nexoruta.cache.distributed.requests").ToArray();
        var operations = samples.Where(sample => sample.Instrument == "nexoruta.cache.distributed.operations").ToArray();
        Assert.Equal(10, requests.Length);
        Assert.Equal(12, operations.Length);
        Assert.All(requests, sample => Assert.Equal(0, sample.Value));
        Assert.All(operations, sample => Assert.Equal(0, sample.Value));
        Assert.Contains(requests, sample => Tag(sample, "cache") == DistributedCacheTelemetry.OperatorBranding
            && Tag(sample, "result") == "miss");
        Assert.Contains(requests, sample => Tag(sample, "cache") == DistributedCacheTelemetry.PublishedTariff
            && Tag(sample, "result") == "hit");
    }

    [Fact]
    public async Task SnapshotRoundTripsThroughVersionedEnvelopeWithFiniteAbsoluteExpiration()
    {
        var redis = new InMemoryDistributedCache();
        var access = Access(redis);
        var key = access.Key(DistributedCacheTelemetry.OperatorBranding, "operator:3");
        var snapshot = new Snapshot(Guid.NewGuid(), 3, "NexoRuta");

        var miss = await access.GetAsync<Snapshot>(DistributedCacheTelemetry.OperatorBranding,
            key, _ => true, CancellationToken.None);
        await access.SetAsync(DistributedCacheTelemetry.OperatorBranding, key, snapshot, 900,
            CancellationToken.None);
        var hit = await access.GetAsync<Snapshot>(DistributedCacheTelemetry.OperatorBranding,
            key, value => value.Version == 3, CancellationToken.None);

        Assert.Equal(DistributedCacheLookup.Miss, miss.Lookup);
        Assert.Equal(DistributedCacheLookup.Hit, hit.Lookup);
        Assert.Equal(snapshot, hit.Value);
        Assert.Equal("nexoruta:unit-tests:cache:v1:operator_branding:operator:3", key);
        Assert.NotNull(redis.LastOptions?.AbsoluteExpirationRelativeToNow);
        Assert.InRange(redis.LastOptions!.AbsoluteExpirationRelativeToNow!.Value.TotalSeconds, 810, 900);
        Assert.Null(redis.LastOptions.SlidingExpiration);
    }

    [Fact]
    public async Task InvalidEntryAndRedisFailureReturnErrorForSourceFallback()
    {
        var redis = new InMemoryDistributedCache();
        var access = Access(redis);
        var key = access.Key(DistributedCacheTelemetry.PublishedTariff, "operator:version");
        await access.SetAsync(DistributedCacheTelemetry.PublishedTariff, key,
            new Snapshot(Guid.NewGuid(), 1, "Tarifa"), 21_600, CancellationToken.None);

        var invalid = await access.GetAsync<Snapshot>(DistributedCacheTelemetry.PublishedTariff,
            key, _ => false, CancellationToken.None);
        Assert.Equal(DistributedCacheLookup.Error, invalid.Lookup);

        redis.ThrowOnGet = true;
        var unavailable = await access.GetAsync<Snapshot>(DistributedCacheTelemetry.PublishedTariff,
            key, _ => true, CancellationToken.None);
        Assert.Equal(DistributedCacheLookup.Error, unavailable.Lookup);
        Assert.Null(unavailable.Value);
    }

    [Fact]
    public async Task SerializationFailureDoesNotBreakTheSourceRead()
    {
        var redis = new InMemoryDistributedCache();
        var access = Access(redis);
        var cyclic = new CyclicValue();
        cyclic.Next = cyclic;

        await access.SetAsync(DistributedCacheTelemetry.OperatorBranding,
            access.Key(DistributedCacheTelemetry.OperatorBranding, "operator:4"), cyclic,
            900, CancellationToken.None);

        Assert.Null(redis.LastOptions);
    }

    [Fact]
    public async Task RedisSetFailureDoesNotFailTheSourceRead()
    {
        var redis = new InMemoryDistributedCache { ThrowOnSet = true };
        var access = Access(redis);

        await access.SetAsync(DistributedCacheTelemetry.PublishedTariff,
            access.Key(DistributedCacheTelemetry.PublishedTariff, "operator:version"),
            new Snapshot(Guid.NewGuid(), 1, "Tarifa"), 21_600, CancellationToken.None);

        Assert.Null(redis.LastOptions);
    }

    [Fact]
    public async Task MalformedAndOversizedEntriesFallBackAndOversizedWritesAreSkipped()
    {
        var redis = new InMemoryDistributedCache();
        var access = Access(redis, maxEntryBytes: 1024);
        var key = access.Key(DistributedCacheTelemetry.OperatorBranding, "operator:5");
        const string writeKey = "oversized-write";
        redis.Seed(key, [0x7B, 0x5D]);
        redis.Seed("large-entry", new byte[1025]);

        var malformed = await access.GetAsync<Snapshot>(DistributedCacheTelemetry.OperatorBranding,
            key, _ => true, CancellationToken.None);
        var tooLarge = await access.GetAsync<Snapshot>(DistributedCacheTelemetry.OperatorBranding,
            "large-entry", _ => true, CancellationToken.None);
        await access.SetAsync(DistributedCacheTelemetry.OperatorBranding, writeKey,
            new Snapshot(Guid.NewGuid(), 5, new string('x', 2048)), 900, CancellationToken.None);

        Assert.Equal(DistributedCacheLookup.Error, malformed.Lookup);
        Assert.Equal(DistributedCacheLookup.Error, tooLarge.Lookup);
        Assert.Null(redis.LastOptions);
        Assert.True(redis.Contains(key));
        Assert.False(redis.Contains(writeKey));
    }

    [Fact]
    public async Task DisabledCacheBypassesAndCallerCancellationIsPreserved()
    {
        var disabled = Access(null, enabled: false);
        var bypass = await disabled.GetAsync<Snapshot>(DistributedCacheTelemetry.OperatorBranding,
            "disabled", _ => true, CancellationToken.None);
        Assert.Equal(DistributedCacheLookup.Bypass, bypass.Lookup);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Access(new InMemoryDistributedCache())
            .GetAsync<Snapshot>(DistributedCacheTelemetry.OperatorBranding, "cancelled", _ => true,
                cancellation.Token));
    }

    [Fact]
    public async Task MetricsEmitOneBoundedDecisionPerReadAndTrackBackendOperationsSeparately()
    {
        var samples = new ConcurrentBag<MetricSample>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, activeListener) =>
        {
            if (instrument.Meter.Name == "NexoRuta.Hosting"
                && instrument.Name.StartsWith("nexoruta.cache.distributed.", StringComparison.Ordinal))
                activeListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            samples.Add(new(instrument.Name, value, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            samples.Add(new(instrument.Name, value, tags.ToArray())));
        listener.Start();

        var redis = new InMemoryDistributedCache();
        var access = Access(redis);
        var key = access.Key(DistributedCacheTelemetry.OperatorBranding, "operator:6");
        Assert.Equal(DistributedCacheLookup.Miss,
            (await access.GetAsync<Snapshot>(DistributedCacheTelemetry.OperatorBranding, key,
                _ => true, CancellationToken.None)).Lookup);
        await access.SetAsync(DistributedCacheTelemetry.OperatorBranding, key,
            new Snapshot(Guid.NewGuid(), 6, "Métrica"), 900, CancellationToken.None);
        Assert.Equal(DistributedCacheLookup.Hit,
            (await access.GetAsync<Snapshot>(DistributedCacheTelemetry.OperatorBranding, key,
                _ => true, CancellationToken.None)).Lookup);
        redis.Seed(key, [0x7B]);
        Assert.Equal(DistributedCacheLookup.Error,
            (await access.GetAsync<Snapshot>(DistributedCacheTelemetry.OperatorBranding, key,
                _ => true, CancellationToken.None)).Lookup);
        Assert.Equal(DistributedCacheLookup.Bypass,
            (await Access(null, enabled: false).GetAsync<Snapshot>(DistributedCacheTelemetry.OperatorBranding,
                key, _ => true, CancellationToken.None)).Lookup);

        var decisions = samples.Where(sample => sample.Instrument == "nexoruta.cache.distributed.requests"
            && sample.Value > 0).ToArray();
        Assert.Equal(4, decisions.Length);
        Assert.Equal(new[] { "bypass", "error", "hit", "miss" }, decisions
            .Select(sample => Tag(sample, "result")).OrderBy(value => value, StringComparer.Ordinal).ToArray());
        Assert.All(decisions, sample => Assert.Equal(DistributedCacheTelemetry.OperatorBranding,
            Tag(sample, "cache")));
        Assert.All(decisions, sample => Assert.Equal(1d, sample.Value));

        var operations = samples.Where(sample => sample.Instrument == "nexoruta.cache.distributed.operations"
            && sample.Value > 0).ToArray();
        Assert.Equal(4, operations.Length);
        Assert.Equal(3, operations.Count(sample => Tag(sample, "operation") == "get"
            && Tag(sample, "result") == "success"));
        Assert.Single(operations, sample => Tag(sample, "operation") == "set"
            && Tag(sample, "result") == "success");
        Assert.All(operations, sample => Assert.Equal(DistributedCacheTelemetry.OperatorBranding,
            Tag(sample, "cache")));
    }

    private static DistributedCacheAccess Access(IDistributedCache? cache, bool enabled = true,
        int maxEntryBytes = 4096)
        => new(cache, new DistributedCacheSettings
        {
            Enabled = enabled,
            Namespace = "unit-tests",
            MaxEntryBytes = maxEntryBytes,
            OperationTimeoutMilliseconds = 100
        }, NullLogger<DistributedCacheAccess>.Instance);

    private static string? Tag(MetricSample sample, string name)
        => sample.Tags.Single(tag => tag.Key == name).Value?.ToString();

    private sealed record Snapshot(Guid Id, int Version, string Name);
    private sealed record MetricSample(string Instrument, double Value,
        KeyValuePair<string, object?>[] Tags);

    private sealed class CyclicValue
    {
        public CyclicValue? Next { get; set; }
    }

    private sealed class InMemoryDistributedCache : IDistributedCache
    {
        private readonly ConcurrentDictionary<string, byte[]> entries = new(StringComparer.Ordinal);

        public DistributedCacheEntryOptions? LastOptions { get; private set; }
        public bool ThrowOnGet { get; set; }
        public bool ThrowOnSet { get; set; }

        public bool Contains(string key) => entries.ContainsKey(key);
        public void Seed(string key, byte[] value) => entries[key] = value.ToArray();

        public byte[]? Get(string key) => ThrowOnGet
            ? throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "test outage")
            : entries.TryGetValue(key, out var value) ? value.ToArray() : null;

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Get(key));
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            if (ThrowOnSet) throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "test outage");
            LastOptions = options;
            entries[key] = value.ToArray();
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Set(key, value, options);
            return Task.CompletedTask;
        }

        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key) => entries.TryRemove(key, out _);
        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Remove(key);
            return Task.CompletedTask;
        }
    }
}
