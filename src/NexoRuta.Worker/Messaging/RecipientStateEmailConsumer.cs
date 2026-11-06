using NexoRuta.Infrastructure.Outbox;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace NexoRuta.Worker.Messaging;

public sealed class RecipientStateEmailConsumer(IConfiguration configuration, IServiceScopeFactory scopes,
    ILogger<RecipientStateEmailConsumer> logger) : BackgroundService
{
    public const string Queue = "nexoruta.destinatario.estado.email.v1";
    public const string DeadQueue = "nexoruta.destinatario.estado.email.dead.v1";

    public static async Task DeclareAsync(IChannel channel, CancellationToken ct)
    {
        await channel.QueueDeclareAsync(DeadQueue, true, false, false, cancellationToken: ct);
        await channel.QueueDeclareAsync(Queue, true, false, false, new Dictionary<string, object?>
        {
            ["x-queue-type"] = "quorum",
            ["x-delivery-limit"] = 10,
            ["x-dead-letter-exchange"] = "",
            ["x-dead-letter-routing-key"] = DeadQueue
        }, cancellationToken: ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = RabbitMqWorkerConnection.CreateFactory(configuration);
        await using var connection = await RabbitMqWorkerConnection.ConnectWithRetryAsync(factory, logger, stoppingToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
        await DeclareAsync(channel, stoppingToken);
        await channel.BasicQosAsync(0, 1, false, stoppingToken);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            if (!Guid.TryParse(delivery.BasicProperties.MessageId, out var id) || id == Guid.Empty ||
                delivery.BasicProperties.Type != OutboxMessageTypes.RecipientStateEmail)
            {
                await channel.BasicNackAsync(delivery.DeliveryTag, false, false, stoppingToken);
                return;
            }
            var traceParent = WorkerTelemetry.ReadTraceParent(delivery.BasicProperties.Headers);
            using var activity = WorkerTelemetry.StartConsumerActivity(traceParent, "rabbitmq.recipient-state");
            using var correlation = logger.BeginScope(new Dictionary<string, object?>
            {
                ["correlation_id"] = WorkerTelemetry.CorrelationId(traceParent, delivery.BasicProperties.CorrelationId ?? id.ToString("D")),
                ["message_id"] = id
            });
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<RecipientStateEmailProcessor>().EjecutarAsync(id, stoppingToken);
                await channel.BasicAckAsync(delivery.DeliveryTag, false, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception error)
            {
                logger.LogWarning("Recipient state email failed. MessageId: {MessageId}, ExceptionType: {ExceptionType}", id, error.GetType().Name);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    await channel.BasicNackAsync(delivery.DeliveryTag, false, true, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            }
        };
        await channel.BasicConsumeAsync(Queue, false, consumer, stoppingToken);
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }
}
