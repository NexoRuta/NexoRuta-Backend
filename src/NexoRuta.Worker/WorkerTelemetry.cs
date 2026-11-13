using System.Diagnostics;
using System.Diagnostics.Metrics;
using NexoRuta.Infrastructure.Outbox;

namespace NexoRuta.Worker;

/// <summary>Reports worker progress without message, account, or processor identifiers.</summary>
public static class WorkerTelemetry
{
    private static readonly Meter Meter = new("NexoRuta.Hosting");
    private static readonly ActivitySource ActivitySource = new("NexoRuta.Worker");
    private static readonly TransportProgress[] Transports =
    [
        new("RabbitMq"),
        new("Kafka"),
        new("Push"), new("HttpWebhooks")
    ];
    private static TransportProgress activeTransport = Transports[0];

    private static readonly Counter<long> Publications = Meter.CreateCounter<long>(
        "nexoruta.worker.published", "{message}", "Messages successfully handed to their transport.");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>(
        "nexoruta.worker.failures", "{error}", "Worker processing failures, excluding shutdown cancellation.");
    private static readonly Counter<long> PushFailures = Meter.CreateCounter<long>(
        "nexoruta.worker.push.failures", "{error}", "Failed attempts to deliver push notifications to delivery workers.");
    private static readonly Counter<long> CommerceNotificationFailures = Meter.CreateCounter<long>(
        "nexoruta.commerce.notifications.failed", "{error}", "Failed commerce notifications. Includes HTTP webhook delivery attempts.");
    private static readonly Counter<long> ProofDeadlineSweeps = Meter.CreateCounter<long>(
        "nexoruta.worker.proof_deadline.sweeps", "{sweep}", "Proof deadline sweep outcomes.");
    private static readonly Counter<long> ProofDeadlineShipments = Meter.CreateCounter<long>(
        "nexoruta.worker.proof_deadline.shipments", "{shipment}", "Shipments changed by proof deadline sweeps.");
    private static readonly Counter<long> ProofDeadlineContinuations = Meter.CreateCounter<long>(
        "nexoruta.worker.proof_deadline.continuations", "{batch}", "Completed proof deadline batches with more due work.");
    private static readonly Histogram<double> ProofDeadlineDuration = Meter.CreateHistogram<double>(
        "nexoruta.worker.proof_deadline.duration", "ms", "Proof deadline sweep duration.");
    private static long lastProofDeadlineSuccess;
    private static readonly ObservableGauge<double> ProofDeadlineSuccessAge = Meter.CreateObservableGauge(
        "nexoruta.worker.proof_deadline.last_success.age", ObserveProofDeadlineSuccessAge, "s",
        "Seconds since a completed proof deadline sweep; -1 until the first completion.");
    private static readonly ObservableGauge<double> SqlContactAge = Meter.CreateObservableGauge(
        "nexoruta.worker.sql_contact.age", ObserveSqlContactAge, "s",
        "Seconds since a successful SQL reservation query; -1 until the first query completes.");

    public static void UseTransport(string transport)
        => Volatile.Write(ref activeTransport, GetTransport(transport));

    public static void SqlContact(string transport)
    {
        var progress = GetTransport(transport);
        Interlocked.Exchange(ref progress.LastSqlContact, Stopwatch.GetTimestamp());
    }

    public static void Published(string transport)
        => Publications.Add(1, new KeyValuePair<string, object?>("transport", GetTransport(transport).Name));

    public static void Failed(string transport)
    {
        var name = GetTransport(transport).Name;
        Failures.Add(1, new KeyValuePair<string, object?>("transport", name));
    }

    public static void PushDeliveryFailed() => PushFailures.Add(1);

    public static void CommerceNotificationFailed() => CommerceNotificationFailures.Add(1);

    public static void ProofDeadlineSweep(string outcome, int affected, bool moreWork, double elapsedMilliseconds)
    {
        var transport = Volatile.Read(ref activeTransport).Name;
        var tags = new TagList
        {
            { "job", "proof_validation_deadline" },
            { "outcome", outcome },
            { "transport", transport }
        };
        ProofDeadlineSweeps.Add(1, tags);
        if (affected > 0)
            ProofDeadlineShipments.Add(affected,
                new KeyValuePair<string, object?>("job", "proof_validation_deadline"),
                new KeyValuePair<string, object?>("transport", transport));
        if (moreWork)
            ProofDeadlineContinuations.Add(1,
                new KeyValuePair<string, object?>("job", "proof_validation_deadline"),
                new KeyValuePair<string, object?>("transport", transport));
        ProofDeadlineDuration.Record(elapsedMilliseconds, tags);
        if (outcome == "completed")
            Interlocked.Exchange(ref lastProofDeadlineSuccess, Stopwatch.GetTimestamp());
    }

    public static Activity? StartOutboxActivity(OutboxClaim message)
    {
        var parent = ActivityContext.TryParse(message.TraceParent, traceState: null, out var context)
            ? context
            : default;
        return ActivitySource.StartActivity("outbox.process", ActivityKind.Consumer, parent, tags:
        [
            new KeyValuePair<string, object?>("messaging.system", message.Destino),
            new KeyValuePair<string, object?>("messaging.message.type", message.Tipo)
        ]);
    }

    public static Activity? StartConsumerActivity(string? traceParent, string operation)
    {
        var parent = ActivityContext.TryParse(traceParent, traceState: null, out var context)
            ? context
            : default;
        return ActivitySource.StartActivity(operation, ActivityKind.Consumer, parent);
    }

    public static string CorrelationId(OutboxClaim message)
    {
        if (Activity.Current is { } activity)
            return FormatCorrelationId(activity.TraceId);
        return ActivityContext.TryParse(message.TraceParent, traceState: null, out var parent)
            ? FormatCorrelationId(parent.TraceId)
            : message.Id.ToString("N");
    }

    public static string CorrelationId(string? traceParent, string fallback)
        => (Activity.Current is { } activity ? FormatCorrelationId(activity.TraceId) : null)
           ?? (ActivityContext.TryParse(traceParent, traceState: null, out var parent)
               ? FormatCorrelationId(parent.TraceId)
               : fallback);

    private static string FormatCorrelationId(ActivityTraceId traceId)
        => Guid.TryParseExact(traceId.ToHexString(), "N", out var id)
            ? id.ToString("D")
            : traceId.ToHexString();

    public static string? ReadTraceParent(IDictionary<string, object?>? headers)
    {
        if (headers is null || !headers.TryGetValue("traceparent", out var raw))
            return null;
        var value = raw switch
        {
            byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
            ReadOnlyMemory<byte> bytes => System.Text.Encoding.UTF8.GetString(bytes.Span),
            string text => text,
            _ => null
        };
        return ActivityContext.TryParse(value, traceState: null, out _) ? value : null;
    }

    private static IEnumerable<Measurement<double>> ObserveSqlContactAge()
    {
        var progress = Volatile.Read(ref activeTransport);
        var lastContact = Interlocked.Read(ref progress.LastSqlContact);
        var age = lastContact == 0 ? -1 : Stopwatch.GetElapsedTime(lastContact).TotalSeconds;
        yield return new Measurement<double>(age, new KeyValuePair<string, object?>("transport", progress.Name));
    }

    private static IEnumerable<Measurement<double>> ObserveProofDeadlineSuccessAge()
    {
        var lastSuccess = Interlocked.Read(ref lastProofDeadlineSuccess);
        yield return new Measurement<double>(
            lastSuccess == 0 ? -1 : Stopwatch.GetElapsedTime(lastSuccess).TotalSeconds,
            new KeyValuePair<string, object?>("job", "proof_validation_deadline"));
    }

    private static TransportProgress GetTransport(string transport) => transport switch
    {
        "RabbitMq" => Transports[0],
        "Kafka" => Transports[1],
        "Push" => Transports[2],
        "HttpWebhooks" => Transports[3],
        _ => throw new ArgumentException("Unknown worker transport.", nameof(transport))
    };

    private sealed class TransportProgress(string name)
    {
        public string Name { get; } = name;
        public long LastSqlContact;
    }
}
