using System.Text;
using System.Diagnostics;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.Infrastructure.Outbox;
namespace NexoRuta.Worker.Messaging;

public sealed class CommerceNoticeOutboxHandler(KafkaEnvioCreadoPublisher publisher, string tipo) : IOutboxMessageHandler
{
    public OutboxHandlerRoute Route { get; } = new(tipo, 2, OutboxDestino.Kafka);
    public Task HandleAsync(OutboxClaim message, CancellationToken ct) => publisher.PublicarAvisoComercioAsync(message, ct);
}

public sealed class CommerceNoticeConsumer(IConfiguration configuration, IServiceScopeFactory scopes,
    ILogger<CommerceNoticeConsumer> logger) : BackgroundService
{
    public const string Topic = "nexoruta.comercios.v2";
    protected override Task ExecuteAsync(CancellationToken ct) => Task.Run(async () =>
    {
        var producer = KafkaEnvioCreadoPublisher.CrearConfiguracion(configuration);
        var config = new ConsumerConfig
        { BootstrapServers = producer.BootstrapServers, SecurityProtocol = producer.SecurityProtocol,
          SaslMechanism = producer.SaslMechanism, SaslUsername = producer.SaslUsername, SaslPassword = producer.SaslPassword,
          SslCaLocation = producer.SslCaLocation, AllowAutoCreateTopics = false, GroupId = "nexoruta-commerce-inbox-v2", AutoOffsetReset = AutoOffsetReset.Earliest,
          EnableAutoCommit = false, EnableAutoOffsetStore = false };
        using var consumer = new ConsumerBuilder<string, string>(config).SetLogHandler((_, _) => { }).Build();
        consumer.Subscribe(Topic);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                ConsumeResult<string, string>? message = null;
                Activity? activity = null;
                IDisposable? logScope = null;
                try
                {
                    message = consumer.Consume(ct);
                    var traceParent = WorkerTelemetry.ReadTraceParent(message.Message.Headers);
                    var correlationId = WorkerTelemetry.ReadHeaderText(message.Message.Headers, "correlationId");
                    var eventId = WorkerTelemetry.ReadHeaderText(message.Message.Headers, "eventId") ?? "unknown";
                    activity = WorkerTelemetry.StartConsumerActivity(traceParent, "kafka.commerce_notice.consume");
                    activity?.SetTag("messaging.system", "kafka");
                    activity?.SetTag("messaging.operation", "process");
                    activity?.SetTag("messaging.message.id", eventId);
                    if (correlationId is not null) activity?.SetTag("nexoruta.correlation.id", correlationId);
                    logScope = logger.BeginScope(new Dictionary<string, object?>
                    {
                        ["correlation_id"] = correlationId ?? eventId,
                        ["event_id"] = eventId
                    });
                    if (!message.Message.Headers.TryGetLastBytes("eventId", out var bytes) || !Guid.TryParse(Encoding.UTF8.GetString(bytes), out var id))
                        throw new InvalidDataException("Missing event identity.");
                    if (eventId != id.ToString("D")) throw new InvalidDataException("Event identity header is inconsistent.");
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<NexoRutaDbContext>();
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT nexoruta_app.receive_commerce_notice({id},{message.Message.Value}::jsonb)", ct);
                    consumer.Commit(message);
                    activity?.SetStatus(ActivityStatusCode.Ok);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, "Commerce notice projection failed");
                    logger.LogWarning("Commerce notice was not committed: {ErrorType}", error.GetType().Name);
                    if (message != null) consumer.Seek(message.TopicPartitionOffset);
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
                finally
                {
                    logScope?.Dispose();
                    activity?.Dispose();
                }
            }
        }
        finally { consumer.Close(); }
    }, ct);
}
