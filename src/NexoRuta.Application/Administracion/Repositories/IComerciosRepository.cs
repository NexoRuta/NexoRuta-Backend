using NexoRuta.Application.Administracion.Dtos;

namespace NexoRuta.Application.Administracion.Repositories;

public interface IComerciosRepository
{
    Task<IReadOnlyList<ComercioResumen>> ListarAsync(CancellationToken cancellationToken = default);
    Task<ComercioResumen?> ObtenerAsync(Guid comercioId, CancellationToken cancellationToken = default);
}
