using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Worker;

public sealed class ProofValidationDeadlineWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    ILogger<ProofValidationDeadlineWorker> logger) : BackgroundService
{
    private const int BatchSize = 100;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var fullBatch = false;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<NexoRutaDbContext>();
                var now = clock.GetUtcNow();
                var expired = await db.Database.SqlQuery<int>(
                    $"SELECT nexoruta_app.expire_pending_proof_deadlines({now}, {BatchSize}) AS \"Value\"")
                    .SingleAsync(stoppingToken);
                fullBatch = expired == BatchSize;
                if (expired > 0)
                    logger.LogInformation("Expired {Count} shipment proof-validation deadlines.", expired);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Proof-validation deadline sweep failed.");
            }

            if (!fullBatch)
                await Task.Delay(PollInterval, clock, stoppingToken);
        }
    }
}
