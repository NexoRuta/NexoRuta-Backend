using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace NexoRuta.Api.Core.Health;

public sealed class ProcessLifecycleHealthCheck(IHostApplicationLifetime lifetime) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ready = lifetime.ApplicationStarted.IsCancellationRequested
            && !lifetime.ApplicationStopping.IsCancellationRequested;
        return Task.FromResult(ready
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("The process is starting or draining."));
    }
}
