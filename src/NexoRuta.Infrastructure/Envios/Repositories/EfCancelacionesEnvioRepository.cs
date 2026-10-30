using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Envios.Repositories;
using NexoRuta.Application.Envios.Results;
using NexoRuta.Application.Reparto.Exceptions;
using NexoRuta.Domain.Envios;
using NexoRuta.Infrastructure.Persistence;
using Npgsql;

namespace NexoRuta.Infrastructure.Envios.Repositories;

public sealed class EfCancelacionesEnvioRepository(NexoRutaDbContext db) : ICancelacionesEnvioRepository
{
    public async Task<EnvioCancelado?> CancelarAsync(ContextoUsuario actor, Guid envioId, long version, string motivo, CancellationToken ct)
    {
        await using var tx = await db.BeginOrJoinAsync(ct);
        var envio = await db.Envios.AsNoTracking().SingleOrDefaultAsync(e => e.Id == envioId
            && (actor.ComercioId != null ? e.ComercioId == actor.ComercioId : e.OperadorId == actor.OperadorId), ct);
        if (envio is null) return null;
        if (envio.Estado != EstadoEnvio.Cancelado)
        {
            try { envio.CambiarEstado(EstadoEnvio.Cancelado, version); }
            catch (InvalidOperationException) { throw new ConflictoRepartoException("El envío cambió o ya no permite cancelación; si hubo entrega física corresponde devolución."); }
        }
        try
        {
            // La función limita la escritura del comercio a cancelar su envío; no amplía RLS de rutas/envíos.
            var id = Guid.CreateVersion7(); var aviso = Guid.CreateVersion7(); var mensaje = Guid.CreateVersion7();
            await db.SetShipmentHistoryContextAsync("CancelacionEnvio", id, cancellationToken: ct);
            var auditId = await db.Database.SqlQuery<Guid>($"SELECT nexoruta_app.cancel_shipment({envioId}, {version}, {motivo}, {id}, {aviso}, {mensaje}) AS \"Value\"").SingleAsync(ct);
            var audit = await db.CancelacionesEnvio.AsNoTracking().SingleAsync(c => c.Id == auditId, ct);
            await tx.CommitAsync(ct);
            return new(audit.Id, envioId, audit.VersionEnvio, "Cancelado");
        }
        catch (PostgresException ex) when (ex.SqlState == "NXR02")
        { throw new ConflictoRepartoException("El envío cambió o ya no permite esta cancelación; actualizá su estado. Después de una entrega física corresponde devolución."); }
        catch (PostgresException ex) when (ex.SqlState == "42501") { throw new RepartoNoPermitidoException(); }
    }
}
