using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Seguimiento.Interfaces;
using NexoRuta.Domain.Envios;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Seguimiento.Consultas;

public sealed class EfConsultaEstadoEnvio(
    NexoRutaDbContext db
) : IConsultaEstadoEnvio
{
    public async Task<EstadoEnvio?> ObtenerEstadoAsync(
        Guid operadorId,
        Guid envioId,
        CancellationToken cancellationToken = default)
    {
        var resultado = await db.Envios
            .AsNoTracking()
            .Where(envio =>
                envio.OperadorId == operadorId &&
                envio.Id == envioId)
            .Select(envio => new { envio.Estado })
            .FirstOrDefaultAsync(cancellationToken);

        return resultado?.Estado;
    }
}