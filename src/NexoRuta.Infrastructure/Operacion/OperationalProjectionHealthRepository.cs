using NexoRuta.Application.Operacion.Repositories;
using NexoRuta.Application.Operacion.Results;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Operacion;

public sealed class OperationalProjectionHealthRepository(
    OperationalReadStore readStore,
    NexoRutaDbContext db) : IEstadoProyeccionOperativaRepository
{
    public async Task<EstadoProyeccionOperativa> ReadAsync(
        Guid operatorId, Guid? commerceId, CancellationToken ct)
    {
        if (operatorId == Guid.Empty || commerceId == Guid.Empty)
            throw new ArgumentException("The operational query scope is invalid.");

        if (commerceId is not null)
            await RlsTransaction.SelectOperatorAsync(db, operatorId, ct);

        var health = await readStore.ReadHealthAsync("history", operatorId, ct);
        return new(health.Capturados, health.Documentados, health.Estado, health.VerificadaEnUtc);
    }
}
