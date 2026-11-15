using NexoRuta.Infrastructure.Reparto;
namespace NexoRuta.Worker;

public sealed class DailyClosingWorker(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<DailyClosingWorker> logger) : BackgroundService
{
    private readonly Guid workerId = Guid.NewGuid();
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<DailyClosingExecutor>()
                    .ExecuteNextAsync(workerId, clock.GetUtcNow(), stoppingToken);
                if (result is not null)
                {
                    logger.LogInformation("Daily closing applied by {WorkerId}: operator {OperatorId}, route {RouteId}, operation {OperationId}, cutoff {Cutoff}, completed {Completed}.",
                        workerId, result.OperadorId, result.RutaId, result.Id, result.CorteEnUtc, clock.GetUtcNow());
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError("Daily closing failed for {WorkerId}: {ErrorType}; transaction rolled back, eligible work remains recoverable.", workerId, error.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(30), clock, stoppingToken);
        }
    }
}
