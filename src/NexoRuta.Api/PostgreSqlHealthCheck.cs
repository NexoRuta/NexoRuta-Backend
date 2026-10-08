using Microsoft.Extensions.Diagnostics.HealthChecks;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Api;

public sealed class PostgreSqlHealthCheck(NexoRutaDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy("PostgreSQL está disponible.")
                : HealthCheckResult.Unhealthy("PostgreSQL no está disponible.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("No se pudo conectar a PostgreSQL.", exception);
        }
    }
}
