using Microsoft.Extensions.DependencyInjection;
using NexoRuta.Infrastructure.Outbox;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace NexoRuta.Worker.Messaging;

public sealed class PasswordRecoveryEmailDeadLetterConsumer(
    IConfiguration configuration,
    IServiceScopeFactory scopeFactory,
    ILogger<PasswordRecoveryEmailDeadLetterConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = RabbitMqWorkerConnection.CreateFactory(configuration);
        await using var connection = await RabbitMqWorkerConnection.ConnectWithRetryAsync(factory, logger, stoppingToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
        await PasswordRecoveryEmailTopology.DeclareAsync(channel, stoppingToken);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) => await ProcessAsync(channel, delivery, stoppingToken);
        await channel.BasicConsumeAsync(PasswordRecoveryMessaging.EmailDeadLetterQueue, autoAck: false, consumer, stoppingToken);
        logger.LogInformation("Password recovery dead-letter consumer started.");
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task ProcessAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        if (!Guid.TryParse(delivery.BasicProperties.MessageId, out var messageId) || messageId == Guid.Empty)
        {
            logger.LogWarning("Discarding a dead-lettered recovery command without a valid message identifier.");
            await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
            return;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var outbox = scope.ServiceProvider.GetRequiredService<EfOutboxRepository>();
            var changed = await outbox.MarkEmailDeliveryAsync(messageId, OutboxEmailDeliveryState.Failed, ct);
            if (!changed && await outbox.GetEmailDeliveryStateAsync(messageId, ct) != nameof(OutboxEmailDeliveryState.Failed))
                throw new InvalidOperationException("The terminal email state was not persisted.");
            await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
            logger.LogWarning("Password recovery email reached its dead-letter queue. MessageId: {MessageId}", messageId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try { await channel.BasicNackAsync(delivery.DeliveryTag, false, true, CancellationToken.None); }
            catch (Exception error) { logger.LogWarning("Could not requeue a stopping dead letter. ExceptionType: {ExceptionType}", error.GetType().Name); }
        }
        catch (Exception error)
        {
            logger.LogWarning("Dead-letter state persistence failed. MessageId: {MessageId}, ExceptionType: {ExceptionType}; the record will be retried.",
                messageId, error.GetType().Name);
            try { await channel.BasicNackAsync(delivery.DeliveryTag, false, true, ct); }
            catch (Exception nackError) { logger.LogWarning("Dead-letter disposition failed. ExceptionType: {ExceptionType}", nackError.GetType().Name); }
        }
    }
}
