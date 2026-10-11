using NexoRuta.Domain.Envios;

namespace NexoRuta.Application.Seguimiento.Interfaces;

public interface IConsultaEstadoEnvio
{
    Task<EstadoEnvio?> ObtenerEstadoAsync(
        Guid operadorId,
        Guid envioId,
        CancellationToken cancellationToken = default);
}