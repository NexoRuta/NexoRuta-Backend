using Microsoft.EntityFrameworkCore;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Outbox;

public sealed class EfOutboxRepository(NexoRutaDbContext db)
{
    public async Task<OutboxClaim?> ReservarSiguienteAsync(
        Guid procesadorId,
        OutboxDestino destino,
        CancellationToken cancellationToken = default)
    {
        if (procesadorId == Guid.Empty)
            throw new ArgumentException("El identificador del procesador es obligatorio.", nameof(procesadorId));
        if (!Enum.IsDefined(destino))
            throw new ArgumentOutOfRangeException(nameof(destino));

        return await db.Database.SqlQueryRaw<OutboxClaim>(
            "SELECT * FROM " + RlsSqlFunctionContract.ClaimOutbox + "({0}, {1})", procesadorId, destino.ToString())
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
    }

    public Task<bool> MarcarPublicadoAsync(
        Guid mensajeId,
        Guid procesadorId,
        CancellationToken cancellationToken = default)
        => EjecutarFinalizacionAsync(mensajeId, procesadorId, true, null, cancellationToken);

    public Task<bool> MarcarErrorAsync(
        Guid mensajeId,
        Guid procesadorId,
        string error,
        CancellationToken cancellationToken = default)
    {
        var safeError = string.IsNullOrWhiteSpace(error) ? "Error de publicación" : error[..Math.Min(error.Length, 2000)];
        return EjecutarFinalizacionAsync(mensajeId, procesadorId, false, safeError, cancellationToken);
    }

    private async Task<bool> EjecutarFinalizacionAsync(
        Guid mensajeId,
        Guid procesadorId,
        bool correcto,
        string? error,
        CancellationToken cancellationToken)
    {
        if (mensajeId == Guid.Empty || procesadorId == Guid.Empty)
            return false;
        return await db.Database.SqlQueryRaw<bool>(
            "SELECT " + RlsSqlFunctionContract.FinishOutbox + "({0}, {1}, {2}, {3}) AS \"Value\"",
            mensajeId, procesadorId, correcto, error!)
            .SingleAsync(cancellationToken);
    }
}
