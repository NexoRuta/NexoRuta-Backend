using NexoRuta.Application.Operacion.Results;

namespace NexoRuta.Application.Operacion.Repositories;

public interface IEstadoProyeccionOperativaRepository
{
    Task<EstadoProyeccionOperativa> ReadAsync(Guid operatorId, Guid? commerceId, CancellationToken ct);
}
