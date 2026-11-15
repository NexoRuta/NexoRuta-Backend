using Microsoft.Extensions.Options;
using NexoRuta.Infrastructure.Reparto;
namespace NexoRuta.Worker;

public sealed class DailyClosingOptions
{
    public bool Enabled { get; set; }
    public TimeOnly? CutoffLocalTime { get; set; }
    public string? OfflinePolicy { get; set; }
}
public sealed class DailyClosingWorker(IServiceScopeFactory scopes, TimeProvider clock,
    IOptions<DailyClosingOptions> options, ILogger<DailyClosingWorker> logger) : BackgroundService
{
    private readonly Guid workerId = Guid.NewGuid();
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled) { logger.LogInformation("Daily closing is disabled pending an explicit operational cutoff/offline policy."); return; }
        if (settings.CutoffLocalTime is null || settings.OfflinePolicy != "RejectNewPhysicalOperations")
            throw new InvalidOperationException("Daily closing requires an explicit local cutoff and OfflinePolicy=RejectNewPhysicalOperations.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<DailyClosingExecutor>()
                    .ExecuteNextAsync(workerId, clock.GetUtcNow(), settings.CutoffLocalTime.Value, stoppingToken);
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
