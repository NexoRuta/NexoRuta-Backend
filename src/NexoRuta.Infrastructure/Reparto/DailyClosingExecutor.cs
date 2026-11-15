using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Reparto.Dtos;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Reparto;
using NexoRuta.Infrastructure.Persistence;
namespace NexoRuta.Infrastructure.Reparto;

public sealed class DailyClosingExecutor(NexoRutaDbContext db)
{
    public async Task<DailyClosingClaim?> ExecuteNextAsync(Guid worker, DateTimeOffset now, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var claim = await db.Database.SqlQuery<DailyClosingClaim>(
            $"SELECT * FROM nexoruta_app.claim_daily_closing({worker},{now})").SingleOrDefaultAsync(ct);
        if (claim is null) return null;
        var route = await db.HojasRuta.Include(h => h.Paradas).SingleAsync(h => h.Id == claim.RutaId, ct);
        var ids = route.Paradas.Select(p => p.EnvioId).ToArray();
        var shipments = await db.Envios.Where(e => ids.Contains(e.Id)).ToArrayAsync(ct);
        var parcels = await db.Bultos.Where(b => ids.Contains(b.EnvioId)).ToArrayAsync(ct);
        var detail = CierreJornada.Finalizar(route, shipments, parcels, now);
        var incidents = parcels.Where(b => b.RutaCustodiaId == route.Id && b.AccesoCustodioId == route.RepartidorAccesoId
            && b.Custodia == EstadoCustodiaBulto.Repartidor).OrderBy(b => b.Id)
            .Select(b => new IncidenciaCierreDatos(claim.Id, b.EnvioId, b.Id, b.Codigo, b.Version, now, now)).ToArray();
        await db.SaveChangesAsync(ct);
        var json = JsonSerializer.Serialize(incidents, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (!await db.Database.SqlQuery<bool>(
            $"""SELECT nexoruta_app.finish_daily_closing({claim.Id},{now},{json}::jsonb,{detail}) AS "Value" """).SingleAsync(ct))
            throw new InvalidOperationException("Daily closing lost its transaction claim.");
        await tx.CommitAsync(ct);
        return claim;
    }
}
public sealed class DailyClosingClaim
{
    public Guid Id { get; set; }
    public Guid RutaId { get; set; }
    public Guid OperadorId { get; set; }
    public DateTimeOffset CorteEnUtc { get; set; }
}
