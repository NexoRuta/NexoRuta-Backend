using Microsoft.EntityFrameworkCore;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Reparto;
using NexoRuta.Application.Reparto.Exceptions;
namespace NexoRuta.Infrastructure.Reparto;
public sealed partial class EfRutasRepository
{
    private async Task ValidarCapacidadMixta(HojaRuta hoja, Vehiculo vehiculo, CancellationToken ct)
    {
        var ids = hoja.Paradas.Where(p => p.Activa).Select(p => p.EnvioId).ToArray();
        var retiros = await db.Envios.Where(e => e.OperadorId == hoja.OperadorId && ids.Contains(e.Id) && e.EsRetiro).Select(e => e.Id).ToArrayAsync(ct);
        var bultos = await db.Bultos.Where(b => b.OperadorId == hoja.OperadorId && ids.Contains(b.EnvioId) && b.Resultado == ResultadoBulto.Pendiente).ToArrayAsync(ct);
        if (CapacidadCarga.ExcesoRecorrido(vehiculo, hoja.Paradas.Where(p => p.Activa).OrderBy(p => p.Orden).Select(p => (p.EnvioId, retiros.Contains(p.EnvioId))), bultos) is { } exceso)
            throw new ConflictoRepartoException(exceso);
    }
}
