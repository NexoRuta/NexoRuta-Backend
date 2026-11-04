using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace NexoRuta.Infrastructure.Hosting;

public static class ApiTelemetry
{
    private const string PendingSyncHeader = "X-NexoRuta-Pending-Sync";
    private static readonly Meter Meter = new("NexoRuta.Hosting");
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>(
        "nexoruta.api.requests", "{request}", "HTTP requests received by the API.");
    private static readonly Counter<long> ServerErrors = Meter.CreateCounter<long>(
        "nexoruta.api.errors", "{error}", "HTTP requests completed with a 5xx status.");
    private static readonly Histogram<double> RequestDuration = Meter.CreateHistogram<double>(
        "nexoruta.api.request.duration", "s", "API request duration.");
    private static readonly Histogram<long> PendingSyncOperations = Meter.CreateHistogram<long>(
        "nexoruta.mobile.sync.pending", "{operation}", "Pending local operations reported by the mobile client during synchronization.");
    private static readonly Counter<long> SyncOperations = Meter.CreateCounter<long>(
        "nexoruta.mobile.sync.operations", "{operation}", "Mobile operations accepted or rejected by the API.");
    private static readonly TrafficBucket[] Traffic = Enumerable.Range(0, 5).Select(_ => new TrafficBucket()).ToArray();
    private static readonly object TrafficLock = new();
    private static readonly ObservableGauge<double> ErrorRate = Meter.CreateObservableGauge(
        "nexoruta.api.error_rate", ObserveErrorRate, "%", "Percentage of API requests returning 5xx during the previous five minutes.");

    public static async Task MeasureRequestAsync(HttpContext context, RequestDelegate next)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
            if (route.Length > 120)
                route = route[..120];
            var method = context.Request.Method.Length <= 16 ? context.Request.Method : "HTTP";
            var statusCode = context.Response.StatusCode;
            var tags = new TagList
            {
                { "http.request.method", method },
                { "http.route", route },
                { "http.response.status_code", statusCode }
            };
            var duration = Stopwatch.GetElapsedTime(started).TotalSeconds;
            Requests.Add(1, tags);
            RequestDuration.Record(duration, tags);
            RecordTraffic(statusCode >= 500);
            if (statusCode >= 500)
                ServerErrors.Add(1, tags);

            if (route.TrimStart('/') == "api/mobile/jornadas/{jornadaId:guid}/operaciones"
                && context.User.Identity?.IsAuthenticated == true
                && statusCode == StatusCodes.Status200OK
                && context.Request.Headers.TryGetValue(PendingSyncHeader, out var rawPending)
                && int.TryParse(rawPending, NumberStyles.None, CultureInfo.InvariantCulture, out var pending)
                && pending is >= 0 and <= 10_000)
                PendingSyncOperations.Record(pending);
        }
    }

    public static void SyncOperation(string result)
    {
        var normalized = result is "Aplicada" or "Conflicto" ? result : "Error";
        SyncOperations.Add(1, new KeyValuePair<string, object?>("result", normalized));
    }

    private static IEnumerable<Measurement<double>> ObserveErrorRate()
    {
        var currentMinute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        long requests = 0;
        long errors = 0;
        lock (TrafficLock)
        {
            foreach (var bucket in Traffic)
            {
                if (currentMinute - bucket.Minute is >= 0 and < 5)
                {
                    requests += bucket.Requests;
                    errors += bucket.Errors;
                }
            }
        }

        yield return new Measurement<double>(requests == 0 ? 0 : errors * 100d / requests);
    }

    private static void RecordTraffic(bool failed)
    {
        var minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        var bucket = Traffic[(int)(minute % Traffic.Length)];
        lock (TrafficLock)
        {
            if (bucket.Minute != minute)
            {
                bucket.Minute = minute;
                bucket.Requests = 0;
                bucket.Errors = 0;
            }

            bucket.Requests++;
            if (failed)
                bucket.Errors++;
        }
    }

    private sealed class TrafficBucket
    {
        public long Minute = -1;
        public long Requests;
        public long Errors;
    }
}

public static class CacheTelemetry
{
    private static readonly Meter Meter = new("NexoRuta.Hosting");
    private static long hits;
    private static long misses;
    private static readonly Counter<long> CacheHits = Meter.CreateCounter<long>(
        "nexoruta.cache.hits", "{hit}", "Geocoding cache hits.");
    private static readonly Counter<long> CacheMisses = Meter.CreateCounter<long>(
        "nexoruta.cache.misses", "{miss}", "Geocoding cache misses.");
    private static readonly ObservableGauge<double> CacheHitRatio = Meter.CreateObservableGauge(
        "nexoruta.cache.hit_ratio", ObserveHitRatio, "1", "Cumulative geocoding cache hit ratio since process start.");

    public static void Hit()
    {
        Interlocked.Increment(ref hits);
        CacheHits.Add(1);
    }

    public static void Miss()
    {
        Interlocked.Increment(ref misses);
        CacheMisses.Add(1);
    }

    private static IEnumerable<Measurement<double>> ObserveHitRatio()
    {
        var hitCount = Interlocked.Read(ref hits);
        var missCount = Interlocked.Read(ref misses);
        var total = hitCount + missCount;
        yield return new Measurement<double>(total == 0 ? 0 : hitCount / (double)total);
    }
}

public static class DistributedCacheTelemetry
{
    public const string OperatorBranding = "operator_branding";
    public const string PublishedTariff = "published_tariff";

    private static readonly Meter Meter = new("NexoRuta.Hosting");
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>(
        "nexoruta.cache.distributed.requests", "{request}", "Distributed cache lookup result by business cache.");
    private static readonly Counter<long> Operations = Meter.CreateCounter<long>(
        "nexoruta.cache.distributed.operations", "{operation}", "Distributed cache backend operations.");
    private static readonly Histogram<double> OperationDuration = Meter.CreateHistogram<double>(
        "nexoruta.cache.distributed.operation.duration", "s", "Distributed cache backend operation duration.");

    static DistributedCacheTelemetry()
    {
        foreach (var cache in new[] { OperatorBranding, PublishedTariff })
        foreach (var result in new[] { "hit", "miss", "error", "bypass", "cancelled" })
            Requests.Add(0, new KeyValuePair<string, object?>("cache", cache),
                new KeyValuePair<string, object?>("result", result));
        foreach (var cache in new[] { OperatorBranding, PublishedTariff })
        foreach (var operation in new[] { "get", "set", "remove" })
        foreach (var result in new[] { "success", "error" })
            Operations.Add(0, new KeyValuePair<string, object?>("cache", cache),
                new KeyValuePair<string, object?>("operation", operation),
                new KeyValuePair<string, object?>("result", result));
    }

    public static void Request(string cache, string result)
        => Requests.Add(1, new KeyValuePair<string, object?>("cache", cache),
            new KeyValuePair<string, object?>("result", result));

    public static void Operation(string cache, string operation, bool succeeded, double elapsedSeconds)
    {
        var tags = new TagList
        {
            { "cache", cache },
            { "operation", operation },
            { "result", succeeded ? "success" : "error" }
        };
        Operations.Add(1, tags);
        OperationDuration.Record(elapsedSeconds, new TagList
        {
            { "cache", cache },
            { "operation", operation }
        });
    }
}
