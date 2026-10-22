using System.Text.Json;
using NexoRuta.Infrastructure.Administracion.Autenticacion;
using NexoRuta.Infrastructure.Outbox;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguimiento;
using Microsoft.EntityFrameworkCore;

namespace NexoRuta.IntegrationTests.Messaging;

public sealed class KafkaOutboxReservationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Kafka_PuedeReservarUnEventoPendiente()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        var envio = await SeguimientoTestData.CreateEnvioAsync(db);
        var evento = OutboxMessage.ForShipment(envio.OperadorId, envio.Id, OutboxMessageTypes.ShipmentCreated,
            OutboxDestino.Kafka, "{}");
        db.OutboxMessages.Add(evento);
        await db.SaveChangesAsync();
        var reservado = await new EfOutboxRepository(db).ReservarSiguienteAsync(Guid.NewGuid(), OutboxDestino.Kafka);
        Assert.NotNull(reservado);
        Assert.Equal(evento.Id, reservado.Id);
    }

    [Fact]
    public async Task DosDespachadores_SoloUnoReservaElMismoEvento()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using (var db = database.CreateDbContext())
        {
            var envio = await SeguimientoTestData.CreateEnvioAsync(db);
            db.OutboxMessages.Add(OutboxMessage.ForShipment(envio.OperadorId, envio.Id,
                OutboxMessageTypes.ShipmentCreated, OutboxDestino.Kafka, "{}"));
            await db.SaveChangesAsync();
        }
        await using var primero = database.CreateDbContext();
        await using var segundo = database.CreateDbContext();
        var reservas = await Task.WhenAll(
            new EfOutboxRepository(primero).ReservarSiguienteAsync(Guid.NewGuid(), OutboxDestino.Kafka),
            new EfOutboxRepository(segundo).ReservarSiguienteAsync(Guid.NewGuid(), OutboxDestino.Kafka));
        Assert.Single(reservas, x => x is not null);
        await using var verificacion = database.CreateDbContext();
        Assert.Equal(1, (await verificacion.OutboxMessages.AsNoTracking().SingleAsync()).Intentos);
    }

    [Fact]
    public async Task ReservaVencida_SeRecuperaYSoloNuevoPropietarioPuedeConfirmar()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        var envio = await SeguimientoTestData.CreateEnvioAsync(db);
        var evento = OutboxMessage.ForShipment(envio.OperadorId, envio.Id, OutboxMessageTypes.ShipmentCreated, OutboxDestino.Kafka, "{}");
        db.OutboxMessages.Add(evento);
        await db.SaveChangesAsync();
        var repo = new EfOutboxRepository(db);
        var oldOwner = Guid.NewGuid();
        await repo.ReservarSiguienteAsync(oldOwner, OutboxDestino.Kafka);
        await db.OutboxMessages.ExecuteUpdateAsync(s => s.SetProperty(x => x.ReservaHastaUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));
        var newOwner = Guid.NewGuid();
        var recuperado = await repo.ReservarSiguienteAsync(newOwner, OutboxDestino.Kafka);
        Assert.NotNull(recuperado);
        Assert.Equal(evento.Id, recuperado.Id);
        Assert.Equal(2, recuperado.Intentos);
        Assert.False(await repo.MarcarPublicadoAsync(evento.Id, oldOwner));
        Assert.False(await repo.MarcarErrorAsync(evento.Id, oldOwner, "late error"));
        Assert.True(await repo.MarcarPublicadoAsync(evento.Id, newOwner));
    }

    [Fact]
    public async Task DestinosSeparados_NoReservanEventosDelOtroBroker()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        var envio = await SeguimientoTestData.CreateEnvioAsync(db);
        db.OutboxMessages.AddRange(
            OutboxMessage.ForShipment(envio.OperadorId, envio.Id, OutboxMessageTypes.ShipmentCreated, OutboxDestino.Kafka, "{}"),
            OutboxMessage.ForShipment(envio.OperadorId, envio.Id, OutboxMessageTypes.TrackingRequested, OutboxDestino.RabbitMq, "{}"));
        await db.SaveChangesAsync();
        var repo = new EfOutboxRepository(db);
        Assert.Equal(nameof(OutboxDestino.Kafka), (await repo.ReservarSiguienteAsync(Guid.NewGuid(), OutboxDestino.Kafka))!.Destino);
        Assert.Null(await repo.ReservarSiguienteAsync(Guid.NewGuid(), OutboxDestino.Kafka));
        Assert.Equal(nameof(OutboxDestino.RabbitMq), (await repo.ReservarSiguienteAsync(Guid.NewGuid(), OutboxDestino.RabbitMq))!.Destino);
        Assert.Null(await repo.ReservarSiguienteAsync(Guid.NewGuid(), OutboxDestino.RabbitMq));
    }

    [Fact]
    public async Task PriorityAging_PromotesOldLowPriorityBeforeNewNormalWork()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        var shipment = await SeguimientoTestData.CreateEnvioAsync(db);
        var userId = await db.Usuarios.Select(x => x.Id).FirstAsync();
        var eventId = Guid.CreateVersion7();
        var recovery = OutboxMessage.ForIdentity(userId, OutboxMessageTypes.PasswordRecoveryRequested, OutboxDestino.Kafka,
            JsonSerializer.Serialize(new PasswordRecoveryRequestedV1(eventId, userId, DateTimeOffset.UtcNow), PasswordRecoveryJson.Options), id: eventId);
        var normal = OutboxMessage.ForShipment(shipment.OperadorId, shipment.Id,
            OutboxMessageTypes.ShipmentCreated, OutboxDestino.Kafka, "{}");
        db.OutboxMessages.AddRange(recovery, normal);
        await db.SaveChangesAsync();
        var agedAt = DateTimeOffset.UtcNow.AddMinutes(-11);
        await db.OutboxMessages.Where(x => x.Id == recovery.Id).ExecuteUpdateAsync(update => update
            .SetProperty(x => x.CreadoEnUtc, agedAt)
            .SetProperty(x => x.ProximoIntentoEnUtc, agedAt));

        var claimed = await new EfOutboxRepository(db).ReservarSiguienteAsync(Guid.CreateVersion7(), OutboxDestino.Kafka);

        Assert.NotNull(claimed);
        Assert.Equal(recovery.Id, claimed.Id);
        Assert.Equal(OutboxPriority.Baja.ToString(), claimed.Prioridad);
    }

    [Fact]
    public async Task KafkaPriorityNeverLetsLaterMessageOvertakeSameIdentityKey()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        await SeguimientoTestData.CreateEnvioAsync(db);
        var userId = await db.Usuarios.Select(x => x.Id).FirstAsync();
        var firstId = Guid.CreateVersion7();
        var secondId = Guid.CreateVersion7();
        OutboxMessage Request(Guid id) => OutboxMessage.ForIdentity(userId,
            OutboxMessageTypes.PasswordRecoveryRequested, OutboxDestino.Kafka,
            JsonSerializer.Serialize(new PasswordRecoveryRequestedV1(id, userId, DateTimeOffset.UtcNow), PasswordRecoveryJson.Options), id: id);
        var first = Request(firstId);
        var second = Request(secondId);
        db.OutboxMessages.AddRange(first, second);
        await db.SaveChangesAsync();
        var earlier = DateTimeOffset.UtcNow.AddMinutes(-2);
        await db.OutboxMessages.Where(x => x.Id == firstId).ExecuteUpdateAsync(update => update
            .SetProperty(x => x.CreadoEnUtc, earlier)
            .SetProperty(x => x.ProximoIntentoEnUtc, earlier));
        await db.OutboxMessages.Where(x => x.Id == secondId)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.Prioridad, OutboxPriority.Alta));

        var repository = new EfOutboxRepository(db);
        var processor = Guid.CreateVersion7();
        var firstClaim = await repository.ReservarSiguienteAsync(processor, OutboxDestino.Kafka);
        Assert.Equal(firstId, firstClaim?.Id);
        Assert.Null(await repository.ReservarSiguienteAsync(Guid.CreateVersion7(), OutboxDestino.Kafka));
        Assert.True(await repository.MarcarPublicadoAsync(firstId, processor));
        Assert.Equal(secondId, (await repository.ReservarSiguienteAsync(Guid.CreateVersion7(), OutboxDestino.Kafka))?.Id);
    }
}
