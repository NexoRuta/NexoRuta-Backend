using Microsoft.Extensions.DependencyInjection;
using NexoRuta.Infrastructure.Outbox;
using NexoRuta.Worker.Messaging;

namespace NexoRuta.Worker;

public sealed class SecurityOutboxPublisherWorker(
    IServiceScopeFactory scopeFactory,
    ISecurityOutboxTransport transport,
    ILogger<SecurityOutboxPublisherWorker> logger) : BackgroundService
{
    private readonly Guid processorId = Guid.CreateVersion7();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var outbox = scope.ServiceProvider.GetRequiredService<SecurityOutboxRepository>();
                var message = await outbox.ClaimNextAsync(processorId, transport.Destination, stoppingToken);
                if (message is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }

                try
                {
                    await transport.PublishAsync(message, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception error)
                {
                    var recorded = await outbox.FinishAsync(message.Id, processorId, false, error.GetType().Name, stoppingToken);
                    logger.LogWarning(
                        "Security outbox publication failed. MessageId: {MessageId}, ExceptionType: {ExceptionType}, RetryRecorded: {RetryRecorded}",
                        message.Id, error.GetType().Name, recorded);
                    continue;
                }

                var completed = await outbox.FinishAsync(message.Id, processorId, true, null, stoppingToken);
                logger.LogInformation("Security outbox broker confirmation recorded. MessageId: {MessageId}, Confirmed: {Confirmed}",
                    message.Id, completed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogError("Security outbox iteration failed. ExceptionType: {ExceptionType}", error.GetType().Name);
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }
}
