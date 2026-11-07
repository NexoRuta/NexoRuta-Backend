namespace NexoRuta.Application.Operacion.Repositories;

public interface IHistorialOperativoRepository
{
    Task<IReadOnlyList<EventoOperativo>> QueryAsync(
        Guid operatorId, Guid? commerceId, Guid? shipmentId, string? type,
        DateTimeOffset from, DateTimeOffset to, int offset, int limit, CancellationToken ct);

    Task<IReadOnlyDictionary<string, long>> VolumesAsync(
        Guid operatorId, Guid? commerceId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
}
