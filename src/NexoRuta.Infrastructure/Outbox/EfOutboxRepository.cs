using Microsoft.EntityFrameworkCore;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Outbox;

public sealed class EfOutboxRepository(NexoRutaDbContext db)
{
    public async Task<bool> EnqueuePasswordRecoveryAsync(
        Guid userId, Guid emailMessageId, Guid eventId, string protectedEmailPayload, string eventPayload,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || emailMessageId == Guid.Empty || eventId == Guid.Empty || emailMessageId == eventId)
            throw new ArgumentException("Password recovery message identities must be distinct and non-empty.");
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedEmailPayload);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventPayload);
        return await db.Database.SqlQueryRaw<bool>(
            "SELECT " + RlsSqlFunctionContract.EnqueuePasswordRecovery + "({0}, {1}, {2}, {3}::jsonb, {4}::jsonb) AS \"Value\"",
            userId, emailMessageId, eventId, protectedEmailPayload, eventPayload)
            .SingleAsync(cancellationToken);
    }

    public async Task<OutboxClaim?> ReservarSiguienteAsync(
        Guid procesadorId, OutboxDestino destino, CancellationToken cancellationToken = default)
    {
        if (procesadorId == Guid.Empty)
            throw new ArgumentException("El identificador del procesador es obligatorio.", nameof(procesadorId));
        if (!Enum.IsDefined(destino)) throw new ArgumentOutOfRangeException(nameof(destino));
        return await db.Database.SqlQueryRaw<OutboxClaim>(
                "SELECT * FROM " + RlsSqlFunctionContract.ClaimOutbox + "({0}, {1})", procesadorId, destino.ToString())
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
    }

    public Task<bool> MarcarPublicadoAsync(Guid mensajeId, Guid procesadorId, CancellationToken cancellationToken = default)
        => EjecutarFinalizacionAsync(mensajeId, procesadorId, true, null, cancellationToken);

    public Task<bool> MarcarErrorAsync(Guid mensajeId, Guid procesadorId, string error, CancellationToken cancellationToken = default)
    {
        var safeError = string.IsNullOrWhiteSpace(error) ? "Publication failed" : error[..Math.Min(error.Length, 200)];
        return EjecutarFinalizacionAsync(mensajeId, procesadorId, false, safeError, cancellationToken);
    }

    public async Task<bool> MarkEmailDeliveryAsync(
        Guid messageId, OutboxEmailDeliveryState state, CancellationToken cancellationToken = default)
    {
        if (messageId == Guid.Empty || !Enum.IsDefined(state)) return false;
        return await db.Database.SqlQueryRaw<bool>(
            "SELECT " + RlsSqlFunctionContract.MarkEmailDelivery + "({0}, {1}) AS \"Value\"",
            messageId, state.ToString()).SingleAsync(cancellationToken);
    }

    public Task<string?> GetEmailDeliveryStateAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        if (messageId == Guid.Empty) throw new ArgumentException("Message ID is required.", nameof(messageId));
        return db.Database.SqlQueryRaw<string?>(
            "SELECT " + RlsSqlFunctionContract.GetEmailDeliveryState + "({0}) AS \"Value\"",
            messageId).SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<bool> EjecutarFinalizacionAsync(
        Guid mensajeId, Guid procesadorId, bool correcto, string? error, CancellationToken cancellationToken)
    {
        if (mensajeId == Guid.Empty || procesadorId == Guid.Empty) return false;
        return await db.Database.SqlQueryRaw<bool>(
            "SELECT " + RlsSqlFunctionContract.FinishOutbox + "({0}, {1}, {2}, {3}) AS \"Value\"",
            mensajeId, procesadorId, correcto, error!)
            .SingleAsync(cancellationToken);
    }
}
