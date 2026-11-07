using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Operacion;
using NexoRuta.Infrastructure.Outbox;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.Infrastructure.Operacion;

namespace NexoRuta.Worker.Messaging;

public sealed class OperationalOutboxHandler(KafkaEnvioCreadoPublisher publisher) : IOutboxMessageHandler
{
    public OutboxHandlerRoute Route { get; } = new(EventoOperativo.OutboxType, 1, OutboxDestino.Kafka);
    public Task HandleAsync(OutboxClaim message, CancellationToken ct) => publisher.PublicarOperativoAsync(message, ct);
}

// Distinct hosted consumers own independent offsets and failure recovery.
public abstract class OperationalConsumer(IConfiguration configuration, ILogger logger) : BackgroundService
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    protected abstract string Group { get; }
    protected abstract Task ProjectAsync(EventoOperativo value, string payload, CancellationToken ct);
    protected abstract Task HealthAsync(int partition, int partitions, long lag, CancellationToken ct);
    protected override Task ExecuteAsync(CancellationToken ct) => Task.Run(async () =>
    {
        var producer = KafkaEnvioCreadoPublisher.CrearConfiguracion(configuration);
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = producer.BootstrapServers,
            SecurityProtocol = producer.SecurityProtocol,
            SaslMechanism = producer.SaslMechanism,
            SaslUsername = producer.SaslUsername,
            SaslPassword = producer.SaslPassword,
            SslCaLocation = producer.SslCaLocation,
            GroupId = configuration[$"Operational:{Group}:Group"] ?? Group,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            AllowAutoCreateTopics = false
        }).SetLogHandler((_, _) => { }).Build();
        consumer.Subscribe(configuration["Operational:Topic"] ?? EventoOperativo.Topic);
        var lastHealth = DateTimeOffset.MinValue;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                ConsumeResult<string, string>? message = null;
                try
                {
                    message = consumer.Consume(TimeSpan.FromMilliseconds(500));
                    if (message is not null)
                    {
                        var value = JsonSerializer.Deserialize<EventoOperativo>(message.Message.Value, Json)
                            ?? throw new InvalidDataException("Missing operational payload.");
                        value.Validar();
                        using var correlation = logger.BeginScope(new Dictionary<string, object?> { ["business_correlation_id"] = value.CorrelacionId, ["event_id"] = value.EventId });
                        if (message.Message.Key != value.OperadorId.ToString("D") + ":" + value.EntidadId.ToString("D"))
                            throw new InvalidDataException("Partition key disagrees with envelope.");
                        await ProjectAsync(value, message.Message.Value, ct);
                        consumer.Commit(message);
                    }
                    if (DateTimeOffset.UtcNow - lastHealth > TimeSpan.FromSeconds(2))
                    {
                        foreach (var partition in consumer.Assignment)
                        {
                            var watermarks = consumer.QueryWatermarkOffsets(partition, TimeSpan.FromSeconds(3));
                            var position = consumer.Position(partition);
                            // Restarting an idle group can also leave Position unset;
                            // its broker-confirmed offset remains the durable frontier.
                            if (position.Value < 0 && watermarks.High != watermarks.Low)
                                position = consumer.Committed([partition], TimeSpan.FromSeconds(3)).Single().Offset;
                            // Kafka keeps Position unset until the first record is consumed.
                            // Broker watermarks still prove that an assigned partition is empty.
                            // Unknown position with retained records must remain behind.
                            var lag = position.Value >= 0
                                ? Math.Max(0, watermarks.High.Value - position.Value)
                                : watermarks.High == watermarks.Low ? 0
                                : Math.Max(1, watermarks.High.Value - watermarks.Low.Value);
                            await HealthAsync(partition.Partition.Value, configuration.GetValue("Operational:Partitions", 3), lag, ct);
                        }
                        lastHealth = DateTimeOffset.UtcNow;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    // Fail closed: a malformed/poison event blocks its partition and remains replayable.
                    // Log identity/offset only, never private payload or connection information.
                    logger.LogWarning("Operational projection {Group} blocked at {Offset}: {ErrorType}", Group,
                        message?.TopicPartitionOffset.ToString(), error.GetType().Name);
                    if (message is not null) consumer.Seek(message.TopicPartitionOffset);
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
            }
        }
        finally { consumer.Close(); }
    }, ct);
}

public sealed class OperationalBoardConsumer(IConfiguration configuration, IServiceScopeFactory scopes,
    ILogger<OperationalBoardConsumer> logger) : OperationalConsumer(configuration, logger)
{
    protected override string Group => "nexoruta-operational-board-v1";
    protected override async Task HealthAsync(int partition, int partitions, long lag, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoRutaDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT nexoruta_app.operational_projection_health({"board"},{partition},{partitions},{lag})", ct);
    }
    protected override async Task ProjectAsync(EventoOperativo value, string payload, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoRutaDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT nexoruta_app.project_operational_event({payload}::jsonb)", ct);
    }
}

public sealed class OperationalHistoryConsumer(IConfiguration configuration, OperationalMongoStore store, IServiceScopeFactory scopes,
    ILogger<OperationalHistoryConsumer> logger) : OperationalConsumer(configuration, logger)
{
    protected override string Group => "nexoruta-operational-history-v1";
    protected override async Task HealthAsync(int partition, int partitions, long lag, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoRutaDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT nexoruta_app.operational_projection_health({"history"},{partition},{partitions},{lag})", ct);
    }
    protected override async Task ProjectAsync(EventoOperativo value, string payload, CancellationToken ct)
    {
        await store.InsertAsync(value, payload, ct);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoRutaDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT nexoruta_app.ack_operational_document({value.EventId},{value.OperadorId})", ct);
    }
}
