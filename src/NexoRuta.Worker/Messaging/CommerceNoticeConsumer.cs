using System.Text;
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
                try
                {
                    message = consumer.Consume(ct);
                    if (!message.Message.Headers.TryGetLastBytes("eventId", out var bytes) || !Guid.TryParse(Encoding.UTF8.GetString(bytes), out var id))
                        throw new InvalidDataException("Missing event identity.");
                    using var scope = scopes.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<NexoRutaDbContext>();
                    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT nexoruta_app.receive_commerce_notice({id},{message.Message.Value}::jsonb)", ct);
                    consumer.Commit(message);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    logger.LogWarning("Commerce notice was not committed: {ErrorType}", error.GetType().Name);
                    if (message != null) consumer.Seek(message.TopicPartitionOffset);
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
            }
        }
        finally { consumer.Close(); }
    }, ct);
}
