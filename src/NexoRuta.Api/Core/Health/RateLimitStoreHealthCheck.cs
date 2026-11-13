using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace NexoRuta.Api.Core.Health;

public sealed class RateLimitStoreHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = dataSource.CreateCommand("""
                SELECT pg_catalog.has_schema_privilege(current_user, pg_catalog.to_regnamespace('nexoruta_hosting'), 'USAGE')
                    AND pg_catalog.has_function_privilege(current_user,
                        pg_catalog.to_regprocedure('nexoruta_hosting.take_rate_limit(text,integer)'), 'EXECUTE')
                    AND pg_catalog.to_regclass('nexoruta_hosting.rate_limits') IS NOT NULL
                """);
            command.CommandTimeout = 2;
            return await command.ExecuteScalarAsync(cancellationToken) is true
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("The shared rate-limit store is unavailable.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("The shared rate-limit store is unavailable.");
        }
    }
}
