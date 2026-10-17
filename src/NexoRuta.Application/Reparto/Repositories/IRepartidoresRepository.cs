using NexoRuta.Application.Reparto.Dtos;

namespace NexoRuta.Application.Reparto.Repositories;

public interface IRepartidoresRepository
{
    Task<IReadOnlyList<RepartidorResumen>> ListarAsync(Guid operadorId, CancellationToken ct);
    Task<RepartidorResumen?> ObtenerActivoAsync(Guid operadorId, Guid accesoId, CancellationToken ct);
    Task<RepartidorResumen?> GuardarAsync(Guid operadorId, Guid accesoId, string nombre, bool activo, long? version, CancellationToken ct);
}
