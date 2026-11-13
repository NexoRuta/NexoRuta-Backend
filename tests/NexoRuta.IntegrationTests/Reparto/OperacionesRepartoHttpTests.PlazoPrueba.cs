using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.Domain.Envios;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.Worker;
using Npgsql;
using static NexoRuta.IntegrationTests.Fixtures.RepartoHttpData;

namespace NexoRuta.IntegrationTests.Reparto;

public sealed partial class OperacionesRepartoHttpTests
{
    private static readonly byte[] FotoPrueba = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==");

    [Fact]
    public async Task SixConcurrentProofDeadlineWorkersAdmitOnlyOneSweep()
    {
        var database = await postgres.CreateDatabaseAsync();
        var workerConnectionString = await CreateWorkerClient(database);
        var connections = Enumerable.Range(0, 6).Select(_ => new NpgsqlConnection(workerConnectionString)).ToArray();
        try
        {
            await Task.WhenAll(connections.Select(connection => connection.OpenAsync()));
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var attempts = connections.Select(async connection =>
            {
                await gate.Task;
                return await ExpireResult(connection, DateTimeOffset.UtcNow);
            }).ToArray();
            gate.SetResult();
            var results = await Task.WhenAll(attempts);
            Assert.Single(results, result => result.Status == "Completed");
            Assert.All(results.Where(result => result.Status != "Completed"),
                result => Assert.Contains(result.Status, new[] { "Contended", "NotDue" }));
            Assert.All(results, result => Assert.Equal(0, result.Affected));
        }
        finally
        {
            foreach (var connection in connections) await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task ProofDeadlineOwnerConnectionLossRollsBackCoordinationAndAllowsAnotherWorker()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        var hoja = await Cargada(database, client);
        await using var db = database.CreateDbContext();
        var template = await db.Envios.AsNoTracking().SingleAsync(e => e.Id == hoja.Paradas[0].EnvioId);
        var receipt = DatabasePrecision(DateTimeOffset.UtcNow);
        var shipment = new Envio(template.OperadorId, template.ComercioId, template.CreadoPorUsuarioId,
            template.DestinatarioId, template.DireccionId, template.VersionReglasId, receipt);
        db.Envios.Add(shipment);
        await db.SaveChangesAsync();
        shipment.CambiarEstado(EstadoEnvio.EnDeposito, shipment.Version);
        await db.SaveChangesAsync();
        shipment.CambiarEstado(EstadoEnvio.AsignadoRuta, shipment.Version);
        await db.SaveChangesAsync();
        shipment.CambiarEstado(EstadoEnvio.EnTransito, shipment.Version);
        await db.SaveChangesAsync();
        shipment.DeclararEntregaFisica(receipt, receipt, 60, false, shipment.Version);
        await db.SaveChangesAsync();
        var previousVersion = shipment.Version;
        var deadline = receipt.AddMinutes(60);
        var workerConnectionString = await CreateWorkerClient(database);
        await using var owner = new NpgsqlConnection(workerConnectionString);
        await owner.OpenAsync();
        await using var transaction = await owner.BeginTransactionAsync();
        var first = await ExpireResult(owner, deadline);
        Assert.Equal("Completed", first.Status);
        Assert.Equal(1, first.Affected);

        await using var competitor = new NpgsqlConnection(workerConnectionString);
        await competitor.OpenAsync();
        Assert.Equal("Contended", (await ExpireResult(competitor, deadline)).Status);

        await using var administrator = new NpgsqlConnection(database.ConnectionString);
        await administrator.OpenAsync();
        await using var terminate = new NpgsqlCommand("SELECT pg_terminate_backend(@pid, 5000)", administrator);
        terminate.Parameters.AddWithValue("pid", owner.ProcessID);
        Assert.Equal(true, await terminate.ExecuteScalarAsync());

        var rolledBack = await db.Envios.AsNoTracking().SingleAsync(e => e.Id == shipment.Id);
        Assert.Equal(EstadoEnvio.EntregaPendienteValidacion, rolledBack.Estado);
        Assert.Equal(previousVersion, rolledBack.Version);
        Assert.Equal(0, await db.EnvioEventos.CountAsync(e => e.EnvioId == shipment.Id
            && e.OrigenTipo == "PlazoValidacionPrueba"));

        var recovered = await ExpireResult(competitor, deadline);
        Assert.Equal("Completed", recovered.Status);
        Assert.Equal(1, recovered.Affected);
        Assert.False(recovered.MoreWork);
        terminate.Parameters["pid"].Value = competitor.ProcessID;
        Assert.Equal(true, await terminate.ExecuteScalarAsync());
        await using var successor = new NpgsqlConnection(workerConnectionString);
        await successor.OpenAsync();
        Assert.Equal("NotDue", (await ExpireResult(successor, deadline)).Status);
        await Task.Delay(TimeSpan.FromSeconds(31));
        var replay = await ExpireResult(successor, deadline);
        Assert.Equal("Completed", replay.Status);
        Assert.Equal(0, replay.Affected);
        Assert.False(replay.MoreWork);
        var persisted = await db.Envios.AsNoTracking().SingleAsync(e => e.Id == shipment.Id);
        Assert.Equal(EstadoEnvio.EntregaSinValidacion, persisted.Estado);
        Assert.Equal(previousVersion + 1, persisted.Version);
        Assert.Equal(1, await db.EnvioEventos.CountAsync(e => e.EnvioId == shipment.Id
            && e.OrigenTipo == "PlazoValidacionPrueba"));
    }

    [Fact]
    public async Task ProofDeadlineSweepReportsContentionAndUsesPostgreSqlCooldownClock()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var coordinator = new NpgsqlConnection(database.ConnectionString);
        await coordinator.OpenAsync();
        await using var transaction = await coordinator.BeginTransactionAsync();
        await using (var acquire = new NpgsqlCommand(
                         "SELECT pg_advisory_xact_lock(1314018378, 13)", coordinator, transaction))
            await acquire.ExecuteScalarAsync();

        var workerConnectionString = await CreateWorkerClient(database);
        await using var worker = new NpgsqlConnection(workerConnectionString);
        await worker.OpenAsync();
        await using (var invalidBatch = new NpgsqlCommand(
                         "SELECT * FROM nexoruta_app.expire_pending_proof_deadlines(now(), NULL)", worker))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => invalidBatch.ExecuteNonQueryAsync());
            Assert.Equal("22023", error.SqlState);
        }
        var future = DateTimeOffset.UtcNow.AddYears(1);
        var contended = await ExpireResult(worker, future);
        Assert.Equal("Contended", contended.Status);
        Assert.Equal(0, contended.Affected);
        Assert.False(contended.MoreWork);

        await transaction.RollbackAsync();
        var completed = await ExpireResult(worker, future);
        Assert.Equal("Completed", completed.Status);
        Assert.Equal(0, completed.Affected);
        Assert.False(completed.MoreWork);

        var throttled = await ExpireResult(worker, future);
        Assert.Equal("NotDue", throttled.Status);
        Assert.Equal(0, throttled.Affected);
    }

    [Fact]
    public async Task ProofDeadlineBacklogDrainsAcrossWorkersWithoutCooldownBetweenBatches()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        var hoja = await Cargada(data, client);
        await using var db = data.CreateDbContext();
        var template = await db.Envios.AsNoTracking().SingleAsync(e => e.Id == hoja.Paradas[0].EnvioId);
        var receipts = DatabasePrecision(DateTimeOffset.UtcNow);
        var shipments = Enumerable.Range(0, 251).Select(_ => new Envio(template.OperadorId,
            template.ComercioId, template.CreadoPorUsuarioId, template.DestinatarioId,
            template.DireccionId, template.VersionReglasId, receipts)).ToArray();
        db.Envios.AddRange(shipments);
        await db.SaveChangesAsync();
        foreach (var shipment in shipments) shipment.CambiarEstado(EstadoEnvio.EnDeposito, shipment.Version);
        await db.SaveChangesAsync();
        foreach (var shipment in shipments) shipment.CambiarEstado(EstadoEnvio.AsignadoRuta, shipment.Version);
        await db.SaveChangesAsync();
        foreach (var shipment in shipments) shipment.CambiarEstado(EstadoEnvio.EnTransito, shipment.Version);
        await db.SaveChangesAsync();
        foreach (var shipment in shipments) shipment.DeclararEntregaFisica(receipts, receipts, 60, false, shipment.Version);
        await db.SaveChangesAsync();

        var ids = shipments.Select(shipment => shipment.Id).ToArray();
        var expectedVersion = shipments[0].Version + 1;
        var deadline = receipts.AddMinutes(60);
        var firstWorker = await CreateWorkerClient(data);
        var secondWorker = await CreateWorkerClient(data);
        var first = await ExpireResult(firstWorker, deadline);
        var second = await ExpireResult(secondWorker, deadline);
        var third = await ExpireResult(firstWorker, deadline);
        Assert.Equal(new DeadlineSweepOutcome("Completed", 100, true), first);
        Assert.Equal(new DeadlineSweepOutcome("Completed", 100, true), second);
        Assert.Equal(new DeadlineSweepOutcome("Completed", 51, false), third);
        var replay = await ExpireResult(secondWorker, deadline);
        Assert.Equal(new DeadlineSweepOutcome("NotDue", 0, false), replay);
        var persisted = await db.Envios.AsNoTracking().Where(e => ids.Contains(e.Id)).ToArrayAsync();
        Assert.Equal(251, persisted.Length);
        Assert.All(persisted, shipment =>
        {
            Assert.Equal(EstadoEnvio.EntregaSinValidacion, shipment.Estado);
            Assert.Equal(expectedVersion, shipment.Version);
        });
        Assert.Equal(251, await db.EnvioEventos.CountAsync(e => ids.Contains(e.EnvioId)
            && e.OrigenTipo == "PlazoValidacionPrueba"));
    }

    [Fact]
    public async Task FinJornadaNoBloqueaPruebaCompletaRecibidaAntesDelVencimiento()
    {
        var data = await postgres.CreateDatabaseAsync();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        using var factory = new IntegrationApiFactory(data, settings: new Dictionary<string, string?>
        {
            ["Reparto:EvidenciasEncryptionKey"] = key
        }, timeProvider: clock);
        using var client = factory.CreateHttpClient();
        var hoja = await Cargada(data, client);
        var parada = hoja.Paradas[0];
        var entrega = Operacion(Guid.NewGuid(), 4, "EntregaFisica", parada.EnvioId, parada.EnvioVersion);
        var aceptada = await Aplicar(client, hoja, entrega);
        Assert.Equal("Aplicada", aceptada.Resultado);

        await using var db = data.CreateDbContext();
        var envio = await db.Envios.SingleAsync(x => x.Id == parada.EnvioId);
        Assert.Equal(DatabasePrecision(LlegadaEstimada), envio.EntregaFisicaEnUtc);
        var receipt = Assert.IsType<DateTimeOffset>(envio.EntregaFisicaRecibidaEnUtc);
        Assert.Equal(DatabasePrecision(clock.GetUtcNow()), receipt);
        var vencimiento = Assert.IsType<DateTimeOffset>(envio.PlazoPruebaVenceEnUtc);
        Assert.Equal(receipt.AddMinutes(60), vencimiento);
        Assert.Equal(Json(aceptada), Json(await Aplicar(client, hoja, entrega)));
        Assert.Equal(vencimiento, (await db.Envios.SingleAsync(x => x.Id == envio.Id)).PlazoPruebaVenceEnUtc);

        var parcial = await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 5, "EvidenciaEntrega", envio.Id,
            aceptada.VersionEnvio, new { Nombre = "Receptor" }));
        Assert.Equal("Aplicada", parcial.Resultado);
        Assert.Equal(vencimiento, (await db.Envios.SingleAsync(x => x.Id == envio.Id)).PlazoPruebaVenceEnUtc);
        Assert.Equal("Aplicada", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 6, "FinJornada"))).Resultado);
        Assert.Equal(EstadoEnvio.EntregaPendienteValidacion, (await db.Envios.SingleAsync(x => x.Id == envio.Id)).Estado);

        var fotoId = await SubirFoto(client, hoja, envio.Id);
        clock.Set(vencimiento.AddSeconds(-1));
        var prueba = await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 7, "EvidenciaEntrega", envio.Id,
            parcial.VersionEnvio, new { FotoId = fotoId, Nombre = "Receptor", Latitud = -34.9, Longitud = -56.2,
                PrecisionMetros = 10, PosicionEnUtc = clock.GetUtcNow() }));
        Assert.Equal("Aplicada", prueba.Resultado);
        Assert.True((await db.Envios.AsNoTracking().SingleAsync(x => x.Id == envio.Id)).Estado == EstadoEnvio.Entregado,
            $"Evidence result: {prueba.Detalle}");
        Assert.Equal(vencimiento, (await db.Envios.SingleAsync(x => x.Id == envio.Id)).PlazoPruebaVenceEnUtc);
        Assert.Equal("Finalizada", (await client.GetFromJsonAsync<HojaRutaResponse>($"/api/mobile/rutas/{hoja.Id}"))!.Estado);
    }

    [Fact]
    public async Task EvidenciaEnElLimiteNoEntregaYWorkerRestringidoExpiraUnaVezTrasReinicio()
    {
        var data = await postgres.CreateDatabaseAsync();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        using var factory = new IntegrationApiFactory(data, settings: new Dictionary<string, string?>
        {
            ["Reparto:EvidenciasEncryptionKey"] = key
        }, timeProvider: clock);
        using var client = factory.CreateHttpClient();
        var hoja = await Cargada(data, client);
        var parada = hoja.Paradas[0];
        var entrega = await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 4, "EntregaFisica", parada.EnvioId, parada.EnvioVersion));
        await using var owner = data.CreateDbContext();
        var envio = await owner.Envios.SingleAsync(x => x.Id == parada.EnvioId);
        var vencimiento = Assert.IsType<DateTimeOffset>(envio.PlazoPruebaVenceEnUtc);
        Assert.Equal(Assert.IsType<DateTimeOffset>(envio.EntregaFisicaRecibidaEnUtc).AddMinutes(60), vencimiento);
        Assert.Equal("Aplicada", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 5, "FinJornada"))).Resultado);
        var fotoId = await SubirFoto(client, hoja, envio.Id);

        clock.Set(vencimiento);
        var pruebaId = Guid.NewGuid();
        var prueba = Operacion(pruebaId, 6, "EvidenciaEntrega", envio.Id, entrega.VersionEnvio,
            new { FotoId = fotoId, Nombre = "Receptor", Latitud = -34.9, Longitud = -56.2,
                PrecisionMetros = 10, PosicionEnUtc = clock.GetUtcNow() });

        var workerConnection = await CrearConexionWorker(data);
        await using (var restricted = new NpgsqlConnection(workerConnection))
        {
            await restricted.OpenAsync();
            var visibleRows = (long)(await new NpgsqlCommand(
                "SELECT count(*) FROM public.\"Envios\"", restricted).ExecuteScalarAsync())!;
            Assert.Equal(0, visibleRows); // Worker SQL context does not expose shipments to direct reads.
        }

        var first = await CreateWorkerClient(data);
        var second = await CreateWorkerClient(data);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRun = ExpireWhenReleased(first, gate.Task, vencimiento);
        var secondRun = ExpireWhenReleased(second, gate.Task, vencimiento);
        var evidenceRun = ApplyWhenReleased(client, hoja, prueba, gate.Task);
        gate.SetResult();
        var evidence = await evidenceRun;
        Assert.Equal("Aplicada", evidence.Resultado);
        var results = await Task.WhenAll(firstRun, secondRun);
        Assert.True(results.Sum(result => result.Affected) == 1,
            $"Unexpected sweep outcomes: {string.Join(", ", results.Select(result => $"{result.Status}:{result.Affected}:{result.MoreWork}"))}");
        Assert.Contains(results, result => result.Status == "Completed" && result.Affected == 1);
        Assert.All(results.Where(result => result.Affected == 0), result =>
            Assert.Contains(result.Status, new[] { "Contended", "NotDue" }));

        var final = await owner.Envios.AsNoTracking().SingleAsync(x => x.Id == envio.Id);
        Assert.Equal(EstadoEnvio.EntregaSinValidacion, final.Estado);
        Assert.Equal(envio.Version + 2, final.Version); // evidence fact and one deadline transition
        var expiration = await owner.EnvioEventos.Where(x => x.EnvioId == envio.Id
            && x.OrigenTipo == "PlazoValidacionPrueba").ToArrayAsync();
        var expirationEvent = Assert.Single(expiration);
        Assert.Equal("TransicionEstado", expirationEvent.Tipo);
        Assert.Equal(nameof(EstadoEnvio.EntregaPendienteValidacion), expirationEvent.EstadoAnterior);
        Assert.Equal(nameof(EstadoEnvio.EntregaSinValidacion), expirationEvent.EstadoNuevo);
        Assert.Equal("Proceso", expirationEvent.ActorTipo);
        Assert.Equal("worker:proof-validation-deadline", expirationEvent.ActorProceso);

        // A replay of the committed sweep (including after worker restart) is a no-op.
        var replay = await ExpireResult(first, vencimiento);
        Assert.Contains(replay.Status, new[] { "Completed", "NotDue" });
        Assert.Equal(0, replay.Affected);
        Assert.Equal(1, await owner.EnvioEventos.CountAsync(x => x.EnvioId == envio.Id
            && x.OrigenTipo == "PlazoValidacionPrueba"));

        // Evidence after expiry is retained as a fact but cannot reopen the state.
        var tardia = await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 7, "EvidenciaEntrega", envio.Id,
            evidence.VersionEnvio, new { FotoId = fotoId, Nombre = "Receptor", Latitud = -34.9, Longitud = -56.2,
                PrecisionMetros = 10, PosicionEnUtc = clock.GetUtcNow() }));
        Assert.Equal("Aplicada", tardia.Resultado);
        Assert.Equal(EstadoEnvio.EntregaSinValidacion, (await owner.Envios.AsNoTracking().SingleAsync(x => x.Id == envio.Id)).Estado);
        Assert.Equal("EntregaSinValidacion", tardia.EstadosEnvio[envio.Id]);
    }

    [Fact]
    public async Task PruebaValidaQueGanaElLockNoEsSobrescritaPorBarridoNiCompetidor()
    {
        var data = await postgres.CreateDatabaseAsync();
        var apiName = $"nfr13_api_{Guid.NewGuid():N}";
        var apiConnection = new NpgsqlConnectionStringBuilder(data.ConnectionString) { ApplicationName = apiName };
        var apiData = new IntegrationDatabase(apiConnection.ConnectionString);
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        using var factory = new IntegrationApiFactory(apiData, settings: new Dictionary<string, string?>
        {
            ["Reparto:EvidenciasEncryptionKey"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
        }, timeProvider: clock);
        using var client = factory.CreateHttpClient();
        var hoja = await Cargada(data, client);
        var parada = hoja.Paradas[0];
        var delivery = await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 4, "EntregaFisica", parada.EnvioId, parada.EnvioVersion));
        Assert.Equal("Aplicada", delivery.Resultado);
        Assert.Equal("Aplicada", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 5, "FinJornada"))).Resultado);
        var photo = await SubirFoto(client, hoja, parada.EnvioId);

        await using var db = data.CreateDbContext();
        var before = await db.Envios.AsNoTracking().SingleAsync(e => e.Id == parada.EnvioId);
        var parcelBefore = await db.Bultos.AsNoTracking().SingleAsync(b => b.EnvioId == parada.EnvioId);
        var deadline = Assert.IsType<DateTimeOffset>(before.PlazoPruebaVenceEnUtc);
        clock.Set(deadline.AddSeconds(-1));
        var proof = Operacion(Guid.NewGuid(), 6, "EvidenciaEntrega", parada.EnvioId, delivery.VersionEnvio,
            new { FotoId = photo, Nombre = "Receptor", Latitud = -34.9, Longitud = -56.2,
                PrecisionMetros = 10, PosicionEnUtc = clock.GetUtcNow() });

        await using var blocker = new NpgsqlConnection(data.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand("SELECT 1 FROM public.\"Envios\" WHERE \"Id\"=@id FOR UPDATE", blocker, transaction))
        {
            hold.Parameters.AddWithValue("id", parada.EnvioId);
            Assert.NotNull(await hold.ExecuteScalarAsync());
        }

        var evidence = Aplicar(client, hoja, proof);
        await WaitForDatabaseLock(data, apiName);
        var workerName = $"nfr13_worker_{Guid.NewGuid():N}";
        var workerConnection = new NpgsqlConnectionStringBuilder(await CreateWorkerClient(data)) { ApplicationName = workerName };
        await using var first = await CreateWorkerConnection(workerConnection.ConnectionString);
        var sweep = ExpireResult(first, deadline);
        await WaitForDatabaseLock(data, workerName);
        var competitor = await ExpireResult(await CreateWorkerClient(data), deadline);
        Assert.Equal("Contended", competitor.Status);
        Assert.Equal(0, competitor.Affected);

        await transaction.CommitAsync();
        Assert.Equal("Aplicada", (await evidence).Resultado);
        var completed = await sweep;
        Assert.Equal("Completed", completed.Status);
        Assert.Equal(0, completed.Affected);
        Assert.False(completed.MoreWork);
        var after = await db.Envios.AsNoTracking().SingleAsync(e => e.Id == parada.EnvioId);
        var parcelAfter = await db.Bultos.AsNoTracking().SingleAsync(b => b.EnvioId == parada.EnvioId);
        Assert.Equal(EstadoEnvio.Entregado, after.Estado);
        Assert.Equal(before.Version + 1, after.Version);
        Assert.Equal(ResultadoBulto.Entregado, parcelAfter.Resultado);
        Assert.Equal(parcelBefore.Version + 1, parcelAfter.Version);
        Assert.Equal(0, await db.EnvioEventos.CountAsync(e => e.EnvioId == parada.EnvioId && e.OrigenTipo == "PlazoValidacionPrueba"));
    }

    private static async Task WaitForDatabaseLock(IntegrationDatabase database, string applicationName)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        var limit = DateTimeOffset.UtcNow.AddSeconds(15);
        do
        {
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_stat_activity
                    WHERE application_name = @application AND wait_event_type = 'Lock'
                      AND cardinality(pg_catalog.pg_blocking_pids(pid)) > 0)
                """, connection);
            command.Parameters.AddWithValue("application", applicationName);
            if ((bool)(await command.ExecuteScalarAsync())!) return;
            await Task.Delay(50);
        } while (DateTimeOffset.UtcNow < limit);
        throw new TimeoutException($"The expected database lock was not observed for {applicationName}.");
    }

    [Fact]
    public async Task FalloAlEscribirHistoriaRevierteVencimientoCompleto()
    {
        var data = await postgres.CreateDatabaseAsync();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        using var factory = new IntegrationApiFactory(data, timeProvider: clock);
        using var client = factory.CreateHttpClient();
        var hoja = await Cargada(data, client);
        var parada = hoja.Paradas[0];
        await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 4, "EntregaFisica", parada.EnvioId, parada.EnvioVersion));

        await using var owner = data.CreateDbContext();
        var envio = await owner.Envios.SingleAsync(x => x.Id == parada.EnvioId);
        var version = envio.Version;
        var deadline = Assert.IsType<DateTimeOffset>(envio.PlazoPruebaVenceEnUtc);
        await owner.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION public.fail_proof_deadline_history() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."OrigenTipo" = 'PlazoValidacionPrueba' THEN
                    RAISE EXCEPTION 'injected history failure';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER fail_proof_deadline_history BEFORE INSERT ON public."EnvioEventos"
                FOR EACH ROW EXECUTE FUNCTION public.fail_proof_deadline_history();
            """);
        var worker = await CreateWorkerClient(data);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => Expire(worker, deadline));
        Assert.Contains("injected history failure", failure.MessageText);
        Assert.Equal(EstadoEnvio.EntregaPendienteValidacion, (await owner.Envios.AsNoTracking().SingleAsync(x => x.Id == envio.Id)).Estado);
        Assert.Equal(version, (await owner.Envios.AsNoTracking().SingleAsync(x => x.Id == envio.Id)).Version);
        Assert.Empty(await owner.EnvioEventos.Where(x => x.EnvioId == envio.Id
            && x.OrigenTipo == "PlazoValidacionPrueba").ToArrayAsync());
    }

    [Fact]
    public async Task VersionHistoricaSinPlazoQuedaPendienteYNoEsCandidataDelWorker()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = new IntegrationApiFactory(data);
        using var client = factory.CreateHttpClient();
        var hoja = await Cargada(data, client);
        var parada = hoja.Paradas[0];
        await using var owner = data.CreateDbContext();
        var versionReglasId = await owner.Envios.Where(x => x.Id == parada.EnvioId)
            .Select(x => x.VersionReglasId).SingleAsync();
        await owner.Database.ExecuteSqlInterpolatedAsync($"ALTER TABLE public.\"VersionesReglasOperativas\" DISABLE TRIGGER USER");
        await owner.Database.ExecuteSqlInterpolatedAsync($"UPDATE public.\"VersionesReglasOperativas\" SET \"PlazoValidacionPruebaMinutos\"=NULL WHERE \"Id\"={versionReglasId}");
        await owner.Database.ExecuteSqlInterpolatedAsync($"ALTER TABLE public.\"VersionesReglasOperativas\" ENABLE TRIGGER USER");

        await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 4, "EntregaFisica", parada.EnvioId, parada.EnvioVersion));
        var shipment = await owner.Envios.SingleAsync(x => x.Id == parada.EnvioId);
        Assert.Equal(EstadoEnvio.EntregaPendienteValidacion, shipment.Estado);
        Assert.Null(shipment.PlazoPruebaVenceEnUtc);
        var worker = await CreateWorkerClient(data);
        Assert.Equal(0, await Expire(worker, DateTimeOffset.UtcNow.AddYears(1)));
        Assert.Equal(EstadoEnvio.EntregaPendienteValidacion, (await owner.Envios.AsNoTracking().SingleAsync(x => x.Id == shipment.Id)).Estado);
    }

    [Fact]
    public async Task HostedWorkerProcesaVencimientoConConexionWorkerRestringida()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = new IntegrationApiFactory(data);
        using var client = factory.CreateHttpClient();
        var hoja = await Cargada(data, client);
        var parada = hoja.Paradas[0];
        await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 4, "EntregaFisica", parada.EnvioId, parada.EnvioVersion));
        await using var owner = data.CreateDbContext();
        var deadline = await owner.Envios.AsNoTracking().Where(x => x.Id == parada.EnvioId)
            .Select(x => x.PlazoPruebaVenceEnUtc).SingleAsync();
        var due = Assert.IsType<DateTimeOffset>(deadline);
        Assert.True(due > DateTimeOffset.UtcNow);

        var workerConnection = await CreateWorkerClient(data);
        var workerClock = new ManualClock(due);
        using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<TimeProvider>(workerClock)
            .AddDbContext<NexoRutaDbContext>(options => options.UseNexoRutaPostgres(workerConnection))
            .BuildServiceProvider();
        var worker = new ProofValidationDeadlineWorker(services.GetRequiredService<IServiceScopeFactory>(),
            workerClock, NullLogger<ProofValidationDeadlineWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var limit = DateTimeOffset.UtcNow.AddSeconds(5);
            EstadoEnvio state;
            do
            {
                await Task.Delay(50);
                state = await owner.Envios.AsNoTracking().Where(x => x.Id == parada.EnvioId)
                    .Select(x => x.Estado).SingleAsync();
            } while (state == EstadoEnvio.EntregaPendienteValidacion && DateTimeOffset.UtcNow < limit);
            Assert.Equal(EstadoEnvio.EntregaSinValidacion, state);
            Assert.Single(await owner.EnvioEventos.Where(x => x.EnvioId == parada.EnvioId
                && x.OrigenTipo == "PlazoValidacionPrueba").ToArrayAsync());
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<Guid> SubirFoto(HttpClient client, HojaRutaResponse hoja, Guid envioId)
    {
        var id = Guid.NewGuid();
        using var response = await client.PutAsJsonAsync($"/api/mobile/jornadas/{hoja.JornadaId}/evidencias/{id}",
            new { EnvioId = envioId, Tipo = "Foto", Contenido = FotoPrueba });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return id;
    }

    private static string Json<T>(T value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static DateTimeOffset DatabasePrecision(DateTimeOffset value) =>
        new(value.UtcTicks / 10 * 10, TimeSpan.Zero);

    private static async Task<string> CreateWorkerClient(IntegrationDatabase database)
        => await CrearConexionWorker(database);

    private static async Task<string> CrearConexionWorker(IntegrationDatabase database)
    {
        var role = $"proof_worker_{Guid.NewGuid():N}";
        var password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await using (var owner = new NpgsqlConnection(database.ConnectionString))
        {
            await owner.OpenAsync();
            await using var create = new NpgsqlCommand(
                $"CREATE ROLE {role} LOGIN INHERIT PASSWORD '{password}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS; GRANT nexoruta_worker TO {role};", owner);
            await create.ExecuteNonQueryAsync();
        }
        return new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Username = role, Password = password, Pooling = false
        }.ConnectionString;
    }

    private static async Task<DeadlineSweepOutcome> ExpireWhenReleased(string connectionString, Task released,
        DateTimeOffset now)
    {
        await using var connection = await CreateWorkerConnection(connectionString);
        await released;
        return await ExpireResult(connection, now);
    }

    private static async Task<ResultadoOperacionResponse> ApplyWhenReleased(HttpClient client, HojaRutaResponse hoja,
        object operation, Task released)
    {
        await released;
        return await Aplicar(client, hoja, operation);
    }

    private static async Task<NpgsqlConnection> CreateWorkerConnection(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<int> Expire(string connectionString, DateTimeOffset now)
    {
        await using var connection = await CreateWorkerConnection(connectionString);
        return await Expire(connection, now);
    }

    private static async Task<DeadlineSweepOutcome> ExpireResult(string connectionString, DateTimeOffset now)
    {
        await using var connection = await CreateWorkerConnection(connectionString);
        return await ExpireResult(connection, now);
    }

    private static async Task<int> Expire(NpgsqlConnection connection, DateTimeOffset now)
    {
        var result = await ExpireResult(connection, now);
        if (result.Status == "NotDue")
        {
            // Model the next scheduled worker tick after the database cooldown.
            await Task.Delay(TimeSpan.FromSeconds(31));
            result = await ExpireResult(connection, now);
        }
        return result.Affected;
    }

    private static async Task<DeadlineSweepOutcome> ExpireResult(NpgsqlConnection connection, DateTimeOffset now)
    {
        await using var command = new NpgsqlCommand(
            "SELECT * FROM nexoruta_app.expire_pending_proof_deadlines(@now, 100)", connection);
        command.Parameters.AddWithValue("now", now);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new(reader.GetString(0), reader.GetInt32(1), reader.GetBoolean(2));
    }

    private sealed record DeadlineSweepOutcome(string Status, int Affected, bool MoreWork);

    private sealed class ManualClock(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset current = initial.ToUniversalTime();
        public override DateTimeOffset GetUtcNow() => current;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            TimeProvider.System.CreateTimer(callback, state, dueTime, period);
        public void Set(DateTimeOffset value) => current = value.ToUniversalTime();
    }
}
