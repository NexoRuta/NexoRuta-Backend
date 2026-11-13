using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
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
            var moreWork = false;
            var started = Stopwatch.GetTimestamp();
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<NexoRutaDbContext>();
                var now = clock.GetUtcNow();
                var result = await db.Database.SqlQuery<ProofDeadlineSweepResult>(
                    $"SELECT * FROM nexoruta_app.expire_pending_proof_deadlines({now}, {BatchSize})")
                    .SingleAsync(stoppingToken);
                var outcome = result.Status switch
                {
                    "Completed" => "completed",
                    "Contended" => "contended",
                    "NotDue" => "not_due",
                    _ => throw new InvalidOperationException("Unknown proof deadline sweep result.")
                };
                moreWork = outcome == "completed" && result.MoreWork;
                WorkerTelemetry.ProofDeadlineSweep(outcome, result.Affected, result.MoreWork,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                if (result.Affected > 0)
                    logger.LogInformation("Expired proof deadlines for {Count} shipments.", result.Affected);
                else
                    logger.LogDebug("Proof-deadline sweep returned {Status} (more work: {MoreWork}).",
                        result.Status, result.MoreWork);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                WorkerTelemetry.ProofDeadlineSweep("shutdown", 0, false,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                break;
            }
            catch (Exception exception)
            {
                WorkerTelemetry.ProofDeadlineSweep("failed", 0, false,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                logger.LogError(exception, "Proof-validation deadline sweep failed.");
            }

            if (!moreWork)
                await Task.Delay(PollInterval, clock, stoppingToken);
        }
    }
}
