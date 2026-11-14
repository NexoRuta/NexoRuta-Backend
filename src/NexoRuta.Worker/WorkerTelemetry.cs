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
    private static readonly Counter<long> CommerceNotificationAttemptFailures = Meter.CreateCounter<long>(
        "nexoruta.commerce.notification.attempts.failed", "{attempt}", "Failed HTTP commerce webhook attempts; does not represent terminal deliveries.");
    private static readonly ObservableGauge<long> MobilePendingOperations = Meter.CreateObservableGauge(
        "nexoruta.mobile.sync.pending", ObserveMobilePending, "{operation}",
        "Pending operations from mobile installations reporting within five minutes.");
    private static readonly ObservableGauge<long> MobileSnapshotAvailable = Meter.CreateObservableGauge(
        "nexoruta.mobile.sync.snapshot_available", ObserveSnapshotAvailable, "{state}",
        "One after a successful snapshot query; distinguishes zero from missing telemetry.");
    private static readonly ObservableGauge<long> MobileFreshInstallations = Meter.CreateObservableGauge(
        "nexoruta.mobile.sync.installations.fresh", ObserveFreshInstallations, "{installation}",
        "Mobile installations reporting within five minutes.");
    private static readonly ObservableGauge<long> MobileStaleInstallations = Meter.CreateObservableGauge(
        "nexoruta.mobile.sync.installations.stale", ObserveStaleInstallations, "{installation}",
        "Known mobile installations with a report older than five minutes.");
    private static readonly ObservableGauge<double> MobileOldestReportAge = Meter.CreateObservableGauge(
        "nexoruta.mobile.sync.oldest_report_age", ObserveOldestMobileReport, "s",
        "Age of the oldest retained mobile sync report; zero with no retained reports.");
    private static readonly ObservableGauge<long> WebhookDeliveries = Meter.CreateObservableGauge(
        "nexoruta.commerce.webhooks.deliveries", ObserveWebhookStates, "{delivery}",
        "Persisted HTTP commerce webhook deliveries by current state.");
    private static readonly ObservableGauge<double> OldestPendingWebhookAge = Meter.CreateObservableGauge(
        "nexoruta.commerce.webhooks.oldest_pending_age", ObserveOldestPendingWebhook, "s",
        "Age of the oldest pending, retrying, or processing commerce webhook.");
    private static ObservabilitySnapshot snapshot = ObservabilitySnapshot.Empty;
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

    public static void CommerceNotificationAttemptFailed() => CommerceNotificationAttemptFailures.Add(1);

    public static void SetObservabilitySnapshot(ObservabilitySnapshot value)
        => Volatile.Write(ref snapshot, value);

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

    private static IEnumerable<Measurement<long>> ObserveMobilePending()
        => [new Measurement<long>(Volatile.Read(ref snapshot).PendingSyncOperations)];

    private static IEnumerable<Measurement<long>> ObserveFreshInstallations()
        => [new Measurement<long>(Volatile.Read(ref snapshot).FreshMobileInstallations)];

    private static IEnumerable<Measurement<long>> ObserveStaleInstallations()
        => [new Measurement<long>(Volatile.Read(ref snapshot).StaleMobileInstallations)];

    private static IEnumerable<Measurement<double>> ObserveOldestMobileReport()
        => [new Measurement<double>(Volatile.Read(ref snapshot).OldestMobileSyncReportSeconds)];

    private static IEnumerable<Measurement<double>> ObserveOldestPendingWebhook()
        => [new Measurement<double>(Volatile.Read(ref snapshot).OldestPendingWebhookSeconds)];

    private static IEnumerable<Measurement<long>> ObserveSnapshotAvailable()
        => [new Measurement<long>(Volatile.Read(ref snapshot).SnapshotAvailable ? 1 : 0)];

    private static IEnumerable<Measurement<long>> ObserveWebhookStates()
    {
        var current = Volatile.Read(ref snapshot);
        yield return new Measurement<long>(current.PendingWebhooks, new KeyValuePair<string, object?>("state", "pending"));
        yield return new Measurement<long>(current.RetryWebhooks, new KeyValuePair<string, object?>("state", "retry_or_processing"));
        yield return new Measurement<long>(current.DeliveredWebhooks, new KeyValuePair<string, object?>("state", "delivered"));
        yield return new Measurement<long>(current.FailedWebhooks, new KeyValuePair<string, object?>("state", "failed"));
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

    public static string? ReadTraceParent(Confluent.Kafka.Headers? headers)
    {
        if (headers is null || !headers.TryGetLastBytes("traceparent", out var bytes)) return null;
        var value = System.Text.Encoding.UTF8.GetString(bytes);
        return ActivityContext.TryParse(value, traceState: null, out _) ? value : null;
    }

    public static string? ReadHeaderText(Confluent.Kafka.Headers? headers, string name)
        => headers is not null && headers.TryGetLastBytes(name, out var bytes)
            ? System.Text.Encoding.UTF8.GetString(bytes)
            : null;

    public static string? ReadBusinessCorrelationId(string payload)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty("correlacionId", out var value)
                   && value.ValueKind == System.Text.Json.JsonValueKind.String
                   && Guid.TryParse(value.GetString(), out var id)
                ? id.ToString("D")
                : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
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

public sealed class ObservabilitySnapshot
{
    public long PendingSyncOperations { get; set; }
    public long FreshMobileInstallations { get; set; }
    public long StaleMobileInstallations { get; set; }
    public double OldestMobileSyncReportSeconds { get; set; }
    public double OldestPendingWebhookSeconds { get; set; }
    public long PendingWebhooks { get; set; }
    public long RetryWebhooks { get; set; }
    public long DeliveredWebhooks { get; set; }
    public long FailedWebhooks { get; set; }
    public bool SnapshotAvailable { get; set; }
    public static ObservabilitySnapshot Empty { get; } = new();
}
