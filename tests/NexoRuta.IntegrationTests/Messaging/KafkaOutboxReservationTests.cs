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
        var evento = new OutboxMessage(envio.OperadorId, envio.Id, "EnvioCreado",
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
            db.OutboxMessages.Add(new OutboxMessage(envio.OperadorId, envio.Id,
                "EnvioCreado", OutboxDestino.Kafka, "{}"));
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
        var evento = new OutboxMessage(envio.OperadorId, envio.Id, "EnvioCreado", OutboxDestino.Kafka, "{}");
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
            new OutboxMessage(envio.OperadorId, envio.Id, "EnvioCreado", OutboxDestino.Kafka, "{}"),
            new OutboxMessage(envio.OperadorId, envio.Id, "GenerarSeguimiento", OutboxDestino.RabbitMq, "{}"));
        await db.SaveChangesAsync();
        var repo = new EfOutboxRepository(db);
        Assert.Equal(OutboxDestino.Kafka, (await repo.ReservarSiguienteAsync(Guid.NewGuid(), OutboxDestino.Kafka))!.Destino);
        Assert.Null(await repo.ReservarSiguienteAsync(Guid.NewGuid(), OutboxDestino.Kafka));
        Assert.Equal(OutboxDestino.RabbitMq, (await repo.ReservarSiguienteAsync(Guid.NewGuid(), OutboxDestino.RabbitMq))!.Destino);
        Assert.Null(await repo.ReservarSiguienteAsync(Guid.NewGuid(), OutboxDestino.RabbitMq));
    }
}
