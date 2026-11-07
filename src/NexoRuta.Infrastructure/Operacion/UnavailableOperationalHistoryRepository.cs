using NexoRuta.Application.Operacion;
using NexoRuta.Application.Operacion.Exceptions;
using NexoRuta.Application.Operacion.Repositories;

namespace NexoRuta.Infrastructure.Operacion;

public sealed class UnavailableOperationalHistoryRepository : IHistorialOperativoRepository
{
    public Task<IReadOnlyList<EventoOperativo>> QueryAsync(
        Guid operatorId, Guid? commerceId, Guid? shipmentId, string? type,
        DateTimeOffset from, DateTimeOffset to, int offset, int limit, CancellationToken ct)
        => throw new HistorialOperativoNoDisponibleException();

    public Task<IReadOnlyDictionary<string, long>> VolumesAsync(
        Guid operatorId, Guid? commerceId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
        => throw new HistorialOperativoNoDisponibleException();
}
