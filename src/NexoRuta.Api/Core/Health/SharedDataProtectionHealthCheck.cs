using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NexoRuta.Infrastructure.Hosting;

namespace NexoRuta.Api.Core.Health;

public sealed class SharedDataProtectionHealthCheck(SharedKeyDbContext keyDb) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await keyDb.DataProtectionKeys.AsNoTracking()
                .OrderBy(key => key.Id)
                .Select(key => key.Id)
                .Take(1)
                .ToListAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("The shared Data Protection key store is unavailable.");
        }
    }
}
