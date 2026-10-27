using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace NexoRuta.Worker;

/// <summary>Reports worker progress without message, account, or processor identifiers.</summary>
public static class WorkerTelemetry
{
    private static readonly Meter Meter = new("NexoRuta.Hosting");
    private static readonly TransportProgress[] Transports =
    [
        new("RabbitMq"),
        new("Kafka"),
        new("Push")
    ];

    private static readonly Counter<long> Publications = Meter.CreateCounter<long>(
        "nexoruta.worker.published", "{message}", "Messages successfully handed to their transport.");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>(
        "nexoruta.worker.failures", "{error}", "Worker processing failures, excluding shutdown cancellation.");
    private static readonly ObservableGauge<double> SqlContactAge = Meter.CreateObservableGauge(
        "nexoruta.worker.sql_contact.age", ObserveSqlContactAge, "s",
        "Seconds since a successful SQL reservation query; -1 until the first query completes.");

    public static void SqlContact(string transport)
    {
        var progress = GetTransport(transport);
        Interlocked.Exchange(ref progress.LastSqlContact, Stopwatch.GetTimestamp());
    }

    public static void Published(string transport)
        => Publications.Add(1, new KeyValuePair<string, object?>("transport", GetTransport(transport).Name));

    public static void Failed(string transport)
        => Failures.Add(1, new KeyValuePair<string, object?>("transport", GetTransport(transport).Name));

    private static IEnumerable<Measurement<double>> ObserveSqlContactAge()
    {
        foreach (var progress in Transports)
        {
            var lastContact = Interlocked.Read(ref progress.LastSqlContact);
            var age = lastContact == 0 ? -1 : Stopwatch.GetElapsedTime(lastContact).TotalSeconds;
            yield return new Measurement<double>(age, new KeyValuePair<string, object?>("transport", progress.Name));
        }
    }

    private static TransportProgress GetTransport(string transport) => transport switch
    {
        "RabbitMq" => Transports[0],
        "Kafka" => Transports[1],
        "Push" => Transports[2],
        _ => throw new ArgumentException("Unknown worker transport.", nameof(transport))
    };

    private sealed class TransportProgress(string name)
    {
        public string Name { get; } = name;
        public long LastSqlContact;
    }
}
