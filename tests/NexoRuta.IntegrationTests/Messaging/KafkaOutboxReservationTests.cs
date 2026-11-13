using System.Text.Json;
using NexoRuta.Infrastructure.Administracion.Autenticacion;
using NexoRuta.Infrastructure.Outbox;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguimiento;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.Worker;
using NexoRuta.Worker.Messaging;
using Npgsql;

namespace NexoRuta.IntegrationTests.Messaging;

public sealed class KafkaOutboxReservationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task StoppingWorkerDuringPublishLeavesReservationForAnotherWorker()
    {
        var database = await postgres.CreateDatabaseAsync();
        Guid eventId;
        await using (var owner = database.CreateDbContext())
        {
            var shipment = await SeguimientoTestData.CreateEnvioAsync(owner);
            var message = OutboxMessage.ForShipment(shipment.OperadorId, shipment.Id,
                OutboxMessageTypes.ShipmentCreated, OutboxDestino.Kafka, "{}");
            eventId = message.Id;
            owner.OutboxMessages.Add(message);
            await owner.SaveChangesAsync();
            await DeferGeneratedWorkAsync(owner);
        }
        var runtime = await RestrictedWorkerConnection(database);
        var blocked = new ControlledHandler(block: true);
        await using (var services = WorkerServices(runtime, blocked))
        using (var worker = new KafkaOutboxWorker(services.GetRequiredService<IServiceScopeFactory>(),
                   services.GetRequiredService<OutboxHandlerRegistry>(), NullLogger<KafkaOutboxWorker>.Instance))
        {
            await worker.StartAsync(CancellationToken.None);
            try
            {
                Assert.Equal(eventId, await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(15)));
                await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { await worker.StopAsync(CancellationToken.None); }
        }
        Guid oldOwner;
        await using (var owner = database.CreateDbContext())
        {
            var pending = await owner.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == eventId);
            Assert.Equal(OutboxEstado.Procesando, pending.Estado);
            Assert.Null(pending.PublicadoEnUtc);
            oldOwner = Assert.IsType<Guid>(pending.ProcesadorId);
            Assert.True(pending.ReservaHastaUtc > DateTimeOffset.UtcNow);
            await owner.OutboxMessages.Where(x => x.Id == eventId).ExecuteUpdateAsync(update =>
                update.SetProperty(x => x.ReservaHastaUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }
        var completed = new ControlledHandler(block: false);
        await using (var services = WorkerServices(runtime, completed))
        using (var worker = new KafkaOutboxWorker(services.GetRequiredService<IServiceScopeFactory>(),
                   services.GetRequiredService<OutboxHandlerRegistry>(), NullLogger<KafkaOutboxWorker>.Instance))
        {
            await worker.StartAsync(CancellationToken.None);
            try
            {
                Assert.Equal(eventId, await completed.Started.Task.WaitAsync(TimeSpan.FromSeconds(15)));
                await using var verify = database.CreateDbContext();
                var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
                OutboxMessage row;
                do
                {
                    row = await verify.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == eventId);
                    if (row.Estado == OutboxEstado.Publicado) break;
                    await Task.Delay(50);
                } while (DateTimeOffset.UtcNow < deadline);
                Assert.Equal(OutboxEstado.Publicado, row.Estado);
                Assert.Equal(2, row.Intentos);
                Assert.Null(row.ProcesadorId);
                Assert.Null(row.ReservaHastaUtc);
            }
            finally { await worker.StopAsync(CancellationToken.None); }
        }
        await using var restricted = new NexoRutaDbContext(new DbContextOptionsBuilder<NexoRutaDbContext>()
            .UseNexoRutaPostgres(runtime).Options);
        Assert.False(await new EfOutboxRepository(restricted).MarcarPublicadoAsync(eventId, oldOwner));
    }

    private static ServiceProvider WorkerServices(string connection, ControlledHandler handler)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<NexoRutaDbContext>(options => options.UseNexoRutaPostgres(connection));
        services.AddScoped<EfOutboxRepository>();
        services.AddSingleton<IOutboxMessageHandler>(handler);
        services.AddSingleton<OutboxHandlerRegistry>();
        return services.BuildServiceProvider();
    }

    private static async Task<string> RestrictedWorkerConnection(IntegrationDatabase database)
    {
        var role = $"nfr13_outbox_{Guid.NewGuid():N}";
        var password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await using var sql = new NpgsqlConnection(database.ConnectionString);
        await sql.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE ROLE {role} LOGIN INHERIT PASSWORD '{password}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS; GRANT nexoruta_worker TO {role};", sql);
        await create.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(database.ConnectionString) { Username = role, Password = password, Pooling = false }.ConnectionString;
    }

    private sealed class ControlledHandler(bool block) : IOutboxMessageHandler
    {
        public TaskCompletionSource<Guid> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public OutboxHandlerRoute Route => new(OutboxMessageTypes.ShipmentCreated, OutboxContractVersions.Current, OutboxDestino.Kafka);
        public async Task HandleAsync(OutboxClaim message, CancellationToken cancellationToken)
        {
            Started.TrySetResult(message.Id);
            if (block) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

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
        await DeferGeneratedWorkAsync(db);
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
        await DeferGeneratedWorkAsync(db);
        }
        await using var primero = database.CreateDbContext();
        await using var segundo = database.CreateDbContext();
        var reservas = await Task.WhenAll(
            new EfOutboxRepository(primero).ReservarSiguienteAsync(Guid.NewGuid(), OutboxDestino.Kafka),
            new EfOutboxRepository(segundo).ReservarSiguienteAsync(Guid.NewGuid(), OutboxDestino.Kafka));
        Assert.Single(reservas, x => x is not null);
        await using var verificacion = database.CreateDbContext();
        Assert.Equal(1, (await verificacion.OutboxMessages.AsNoTracking().SingleAsync(x => x.Tipo == OutboxMessageTypes.ShipmentCreated)).Intentos);
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
        await DeferGeneratedWorkAsync(db);
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
        await DeferGeneratedWorkAsync(db);
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
        await DeferGeneratedWorkAsync(db);
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
        await DeferGeneratedWorkAsync(db);
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
    private static Task<int> DeferGeneratedWorkAsync(NexoRuta.Infrastructure.Persistence.NexoRutaDbContext db)
    {
        // These reservation tests isolate their explicit jobs. Retain automatically
        // captured facts/notices, but schedule them outside the owned test interval.
        // Real publication and consumer recovery are exercised in operational tests.
        return db.OutboxMessages.Where(x => x.Tipo == "EventoOperativo" || x.Tipo == OutboxMessageTypes.RecipientStateEmail)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.ProximoIntentoEnUtc, DateTimeOffset.UtcNow.AddDays(1)));
    }

}
