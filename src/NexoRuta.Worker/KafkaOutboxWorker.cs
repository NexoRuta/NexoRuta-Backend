using Confluent.Kafka;
using NexoRuta.Infrastructure.Outbox;
using NexoRuta.Worker.Messaging;

namespace NexoRuta.Worker;

public sealed class KafkaOutboxWorker(
    IServiceScopeFactory scopeFactory,
    KafkaEnvioCreadoPublisher publisher,
    ILogger<KafkaOutboxWorker> logger) : BackgroundService
{
    private readonly Guid procesadorId = Guid.NewGuid();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var repository = scope.ServiceProvider.GetRequiredService<EfOutboxRepository>();
                var evento = await repository.ReservarSiguienteAsync(procesadorId, OutboxDestino.Kafka, stoppingToken);
                if (evento is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }

                try
                {
                    await publisher.PublicarAsync(evento, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // The in-flight publish may succeed; do not delete or complete the lease.
                    break;
                }
                catch (Exception error)
                {
                    var code = error is KafkaException kafka ? $"Kafka:{kafka.Error.Code}" : error.GetType().Name;
                    var recorded = await repository.MarcarErrorAsync(evento.Id, procesadorId, code, stoppingToken);
                    logger.LogWarning(
                        "Kafka outbox delivery failed. EventId: {EventId}, ErrorCode: {ErrorCode}, Attempt: {Attempt}, ErrorRecorded: {Recorded}",
                        evento.Id, code, evento.Intentos, recorded);
                    continue;
                }

                var completed = await repository.MarcarPublicadoAsync(evento.Id, procesadorId, stoppingToken);
                if (completed)
                    logger.LogInformation("Kafka outbox published. EventId: {EventId}", evento.Id);
                else
                    logger.LogWarning("Kafka confirmed {EventId}, but lease completion failed; replay may duplicate the event.", evento.Id);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogError("Kafka outbox iteration failed. ErrorType: {ErrorType}", error.GetType().Name);
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }
}
