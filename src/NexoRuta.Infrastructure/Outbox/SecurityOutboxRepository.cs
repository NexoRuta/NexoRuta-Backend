using Microsoft.EntityFrameworkCore;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Outbox;

public sealed class SecurityOutboxRepository(NexoRutaDbContext db)
{
    public Task<SecurityOutboxClaim?> ClaimNextAsync(
        Guid processorId,
        SecurityOutboxDestination destination,
        CancellationToken cancellationToken = default)
    {
        if (processorId == Guid.Empty) throw new ArgumentException("Processor ID is required.", nameof(processorId));
        if (!Enum.IsDefined(destination)) throw new ArgumentOutOfRangeException(nameof(destination));
        return db.Database.SqlQueryRaw<SecurityOutboxClaim>(
                "SELECT * FROM " + RlsSqlFunctionContract.ClaimSecurityOutbox + "({0}, {1})",
                processorId, destination.ToString())
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> FinishAsync(
        Guid messageId, Guid processorId, bool published, string? error,
        CancellationToken cancellationToken = default)
    {
        if (messageId == Guid.Empty || processorId == Guid.Empty) return false;
        var safeError = string.IsNullOrWhiteSpace(error) ? "Publication failed" : error[..Math.Min(error.Length, 200)];
        return await db.Database.SqlQueryRaw<bool>(
            "SELECT " + RlsSqlFunctionContract.FinishSecurityOutbox + "({0}, {1}, {2}, {3}) AS \"Value\"",
            messageId, processorId, published, safeError).SingleAsync(cancellationToken);
    }

    public async Task<bool> MarkEmailDeliveryAsync(
        Guid messageId, PasswordRecoveryEmailDeliveryState state,
        CancellationToken cancellationToken = default)
    {
        if (messageId == Guid.Empty || state == PasswordRecoveryEmailDeliveryState.Pending) return false;
        return await db.Database.SqlQueryRaw<bool>(
            "SELECT " + RlsSqlFunctionContract.MarkPasswordRecoveryDelivery + "({0}, {1}) AS \"Value\"",
            messageId, state.ToString()).SingleAsync(cancellationToken);
    }

    public Task<string?> GetEmailDeliveryStateAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        if (messageId == Guid.Empty) throw new ArgumentException("Message ID is required.", nameof(messageId));
        return db.Database.SqlQueryRaw<string?>(
            "SELECT " + RlsSqlFunctionContract.GetPasswordRecoveryDeliveryState + "({0}) AS \"Value\"",
            messageId).SingleOrDefaultAsync(cancellationToken);
    }
}
