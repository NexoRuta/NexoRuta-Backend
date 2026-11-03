using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using NexoRuta.Infrastructure.Hosting;
using StackExchange.Redis;

namespace NexoRuta.Infrastructure.Caching;

public enum DistributedCacheLookup
{
    Hit,
    Miss,
    Error,
    Bypass,
    Cancelled
}

public sealed record DistributedCacheRead<T>(DistributedCacheLookup Lookup, T? Value);

public sealed class DistributedCacheAccess(
    IDistributedCache? cache,
    DistributedCacheSettings settings,
    ILogger<DistributedCacheAccess> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool Enabled => settings.Enabled;
    public DistributedCacheSettings Settings => settings;

    public string Key(string cacheCase, string resourceId)
        => $"nexoruta:{settings.Namespace}:cache:v1:{cacheCase}:{resourceId}";

    public void Bypass(string cacheCase)
        => DistributedCacheTelemetry.Request(cacheCase, "bypass");

    public async Task<DistributedCacheRead<T>> GetAsync<T>(string cacheCase, string key,
        Func<T, bool> validator, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(validator);
        if (!settings.Enabled || cache is null)
        {
            DistributedCacheTelemetry.Request(cacheCase, "bypass");
            return new(DistributedCacheLookup.Bypass, default);
        }
        if (cancellationToken.IsCancellationRequested)
        {
            DistributedCacheTelemetry.Request(cacheCase, "cancelled");
            cancellationToken.ThrowIfCancellationRequested();
        }

        var timer = Stopwatch.StartNew();
        try
        {
            using var timeout = CreateTimeout(cancellationToken);
            var bytes = await cache.GetAsync(key, timeout.Token);
            DistributedCacheTelemetry.Operation(cacheCase, "get", true, timer.Elapsed.TotalSeconds);
            if (bytes is null)
            {
                DistributedCacheTelemetry.Request(cacheCase, "miss");
                return new(DistributedCacheLookup.Miss, default);
            }
            if (bytes.Length > settings.MaxEntryBytes)
            {
                DistributedCacheTelemetry.Request(cacheCase, "error");
                return new(DistributedCacheLookup.Error, default);
            }

            CacheEnvelope<T>? envelope;
            try { envelope = JsonSerializer.Deserialize<CacheEnvelope<T>>(bytes, JsonOptions); }
            catch (JsonException)
            {
                DistributedCacheTelemetry.Request(cacheCase, "error");
                return new(DistributedCacheLookup.Error, default);
            }
            if (envelope is not { FormatVersion: 1, Value: not null } || !validator(envelope.Value))
            {
                DistributedCacheTelemetry.Request(cacheCase, "error");
                return new(DistributedCacheLookup.Error, default);
            }
            DistributedCacheTelemetry.Request(cacheCase, "hit");
            return new(DistributedCacheLookup.Hit, envelope.Value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DistributedCacheTelemetry.Operation(cacheCase, "get", false, timer.Elapsed.TotalSeconds);
            DistributedCacheTelemetry.Request(cacheCase, "cancelled");
            throw;
        }
        catch (Exception exception) when (IsCacheFailure(exception))
        {
            DistributedCacheTelemetry.Operation(cacheCase, "get", false, timer.Elapsed.TotalSeconds);
            DistributedCacheTelemetry.Request(cacheCase, "error");
            logger.LogWarning("Distributed cache {CacheCase} GET failed with {FailureType}; using the source.",
                cacheCase, exception.GetType().Name);
            return new(DistributedCacheLookup.Error, default);
        }
    }

    public async Task SetAsync<T>(string cacheCase, string key, T value, int ttlSeconds,
        CancellationToken cancellationToken)
    {
        if (!settings.Enabled || cache is null) return;
        if (cancellationToken.IsCancellationRequested) cancellationToken.ThrowIfCancellationRequested();
        var timer = Stopwatch.StartNew();
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new CacheEnvelope<T>(1, value), JsonOptions);
            if (payload.Length > settings.MaxEntryBytes)
            {
                DistributedCacheTelemetry.Operation(cacheCase, "set", false, timer.Elapsed.TotalSeconds);
                return;
            }
            using var timeout = CreateTimeout(cancellationToken);
            var jitteredSeconds = Math.Max(1, (int)Math.Floor(ttlSeconds * (1d - Random.Shared.NextDouble() * 0.1d)));
            await cache.SetAsync(key, payload, new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(jitteredSeconds)
            }, timeout.Token);
            DistributedCacheTelemetry.Operation(cacheCase, "set", true, timer.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DistributedCacheTelemetry.Operation(cacheCase, "set", false, timer.Elapsed.TotalSeconds);
            throw;
        }
        catch (Exception exception) when (IsCacheFailure(exception))
        {
            DistributedCacheTelemetry.Operation(cacheCase, "set", false, timer.Elapsed.TotalSeconds);
            logger.LogWarning("Distributed cache {CacheCase} SET failed with {FailureType}; keeping the source result.",
                cacheCase, exception.GetType().Name);
        }
    }

    private CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.OperationTimeoutMilliseconds);
        return timeout;
    }

    private static bool IsCacheFailure(Exception exception)
        => exception is RedisException or TimeoutException or JsonException or InvalidDataException
            or OperationCanceledException;

    private sealed record CacheEnvelope<T>(int FormatVersion, T Value);
}
