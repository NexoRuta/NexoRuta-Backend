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
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==");

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
        Assert.Equal(1, results.Sum());

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
        Assert.Equal(0, await Expire(first, vencimiento));
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

    private static async Task<int> ExpireWhenReleased(string connectionString, Task released, DateTimeOffset now)
    {
        await using var connection = await CreateWorkerConnection(connectionString);
        await released;
        return await Expire(connection, now);
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

    private static async Task<int> Expire(NpgsqlConnection connection, DateTimeOffset now)
    {
        await using var command = new NpgsqlCommand(
            "SELECT nexoruta_app.expire_pending_proof_deadlines(@now, 100)", connection);
        command.Parameters.AddWithValue("now", now);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private sealed class ManualClock(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset current = initial.ToUniversalTime();
        public override DateTimeOffset GetUtcNow() => current;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            TimeProvider.System.CreateTimer(callback, state, dueTime, period);
        public void Set(DateTimeOffset value) => current = value.ToUniversalTime();
    }
}
