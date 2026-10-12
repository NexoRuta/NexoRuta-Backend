using NexoRuta.Application.Administracion.Dtos;

namespace NexoRuta.Application.Administracion.Repositories;

public interface IOperadoresRepository
{
    Task<IReadOnlyList<OperadorDisponible>> ListarAsync(CancellationToken cancellationToken = default);
    Task<OperadorDisponible?> ObtenerAsync(Guid operadorId, CancellationToken cancellationToken = default);
}
