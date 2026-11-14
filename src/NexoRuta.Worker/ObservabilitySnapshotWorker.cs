using Microsoft.EntityFrameworkCore;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Worker;

public sealed class ObservabilitySnapshotWorker(IServiceScopeFactory scopes, ILogger<ObservabilitySnapshotWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<NexoRutaDbContext>();
                var snapshot = await db.Database.SqlQueryRaw<ObservabilitySnapshot>(
                    "SELECT * FROM nexoruta_app.observability_snapshot()").SingleAsync(stoppingToken);
                snapshot.SnapshotAvailable = true;
                WorkerTelemetry.SetObservabilitySnapshot(snapshot);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                WorkerTelemetry.SetObservabilitySnapshot(ObservabilitySnapshot.Empty);
                logger.LogWarning("Observability database snapshot failed. ErrorType: {ErrorType}", error.GetType().Name);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
