using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Operacion;
using NexoRuta.Infrastructure.Persistence;
namespace NexoRuta.Worker.Messaging;

public sealed class ManagementReportingConsumer(IConfiguration configuration, IServiceScopeFactory scopes,
    ILogger<ManagementReportingConsumer> logger) : OperationalConsumer(configuration,logger)
{
    protected override string Group => "nexoruta-management-v1";
    protected override async Task ProjectAsync(EventoOperativo value,string payload,CancellationToken ct)
    {
        if(value.EnvioId is not { } shipment) return;
        await using var scope=scopes.CreateAsyncScope();
        var db=scope.ServiceProvider.GetRequiredService<NexoRutaDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT nexoruta_app.reconcile_management({value.OperadorId},{shipment},1)",ct);
    }
    protected override async Task HealthAsync(int partition,int partitions,long lag,CancellationToken ct)
    {
        await using var scope=scopes.CreateAsyncScope();
        var db=scope.ServiceProvider.GetRequiredService<NexoRutaDbContext>();
        // Bounded reconciliation also recovers a stopped worker or a notification received before a later commit.
        await db.Database.ExecuteSqlRawAsync("SELECT nexoruta_app.reconcile_management(NULL,NULL,100)",ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT nexoruta_app.operational_projection_health({"management"},{partition},{partitions},{lag})",ct);
    }
}
