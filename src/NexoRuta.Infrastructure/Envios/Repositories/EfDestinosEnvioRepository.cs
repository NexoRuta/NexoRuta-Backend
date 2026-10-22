using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Envios.Dtos;
using NexoRuta.Application.Envios.Repositories;
using NexoRuta.Application.Reparto.Exceptions;
using NexoRuta.Domain.Envios;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Envios.Repositories;

public sealed class EfDestinosEnvioRepository(NexoRutaDbContext db, TimeProvider reloj) : IDestinosEnvioRepository
{
    public Task<DestinoEnvioDatos?> ObtenerAsync(Guid comercioId, Guid envioId, CancellationToken ct)
        => (from e in db.Envios.AsNoTracking() join d in db.Direcciones.AsNoTracking() on e.DireccionId equals d.Id
            where e.Id == envioId && e.ComercioId == comercioId && e.OperadorId == d.OperadorId
            select new DestinoEnvioDatos(e.Id, d.Descripcion, d.Version, e.Estado.ToString(), d.Latitud, d.Longitud, d.ConfirmadaEnUtc)).SingleOrDefaultAsync(ct);

    public async Task<bool> ConfirmarAsync(Guid comercioId, Guid usuarioId, Guid envioId, long version, double latitud, double longitud, CancellationToken ct)
    {
        var actual = await (from e in db.Envios.AsNoTracking() join d in db.Direcciones.AsNoTracking() on e.DireccionId equals d.Id
            where e.Id == envioId && e.ComercioId == comercioId && e.OperadorId == d.OperadorId
            select new { e.Estado, Direccion = d }).SingleOrDefaultAsync(ct);
        if (actual is null) return false;
        if (actual.Direccion.Version != version || actual.Estado != EstadoEnvio.Admitido)
            throw new ConflictoRepartoException("El destino o el envío cambiaron. Recargá antes de confirmar.");
        actual.Direccion.ConfirmarUbicacion(latitud, longitud, usuarioId, reloj.GetUtcNow(), version);
        try
        {
            return await db.Database.SqlQuery<bool>($"SELECT nexoruta_app.confirm_shipment_destination({envioId},{version},{latitud},{longitud}) AS \"Value\"").SingleAsync(ct);
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "NXR02")
        { throw new ConflictoRepartoException("El destino o el envío cambiaron. Recargá antes de confirmar."); }
    }
}
