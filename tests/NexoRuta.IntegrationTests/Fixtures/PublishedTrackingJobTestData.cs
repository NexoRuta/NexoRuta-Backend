using Microsoft.EntityFrameworkCore;
using NexoRuta.Infrastructure.Outbox;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.IntegrationTests.Fixtures;

internal static class PublishedTrackingJobTestData
{
    public static async Task<Guid> PublishInitialTrackingJobAsync(IntegrationDatabase database, Guid shipmentId)
    {
        await using var db = database.CreateDbContext();
        var messageId = await db.OutboxMessages
            .Where(x => x.EnvioId == shipmentId && x.Tipo == "GenerarSeguimiento")
            .Select(x => x.Id)
            .SingleAsync();
        await PublishPendingAsync(db, messageId);
        return messageId;
    }

    public static async Task<Guid> CreatePublishedTrackingJobAsync(
        IntegrationDatabase database, Guid operatorId, Guid shipmentId)
    {
        await using var db = database.CreateDbContext();
        var message = new OutboxMessage(operatorId, shipmentId, "GenerarSeguimiento", OutboxDestino.RabbitMq,
            "{\"envioId\":\"test\"}");
        db.OutboxMessages.Add(message);
        await db.SaveChangesAsync();
        await PublishPendingAsync(db, message.Id);
        return message.Id;
    }

    public static async Task PublishPendingAsync(IntegrationDatabase database, Guid messageId)
    {
        await using var db = database.CreateDbContext();
        await PublishPendingAsync(db, messageId);
    }

    private static async Task PublishPendingAsync(NexoRutaDbContext db, Guid messageId)
    {
        var publisherId = Guid.NewGuid();
        var claimed = await new EfOutboxRepository(db).ReservarSiguienteAsync(publisherId, OutboxDestino.RabbitMq);
        if (claimed?.Id != messageId || !await new EfOutboxRepository(db).MarcarPublicadoAsync(messageId, publisherId))
            throw new InvalidOperationException("The test could not publish the persisted tracking outbox job.");
    }
}
