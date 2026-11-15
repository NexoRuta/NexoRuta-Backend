using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Reparto;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.Infrastructure.Reparto;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.Worker;

namespace NexoRuta.IntegrationTests.Reparto;

public sealed partial class OperacionesRepartoHttpTests
{
    [Fact]
    public async Task HostedDailyWorkersCloseAtLocalCutoffOncePreservingCustodyAndMobileSequence()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var api = data.CreateApiFactory(); using var client = api.CreateHttpClient();
        var route = await Cargada(data, client);
        var cutoff = new DateTimeOffset(route.Fecha.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(-3)).ToUniversalTime();
        var clock = new ClosingClock(cutoff.AddSeconds(-30));
        var connection = await CrearConexionWorker(data);
        await using var services = ClosingServices(connection);
        using var first = ClosingWorker(services, clock); using var second = ClosingWorker(services, clock);
        await first.StartAsync(default); await second.StartAsync(default);
        try
        {
            await WaitClosing(() => Task.FromResult(clock.ActiveTimers >= 2));
            await using var db = data.CreateDbContext();
            Assert.Equal(EstadoHojaRuta.EnCurso, (await db.HojasRuta.AsNoTracking().SingleAsync()).Estado);
            Assert.Equal(0, await ClosingCount(db));
            clock.Advance(TimeSpan.FromSeconds(30));
            await WaitClosing(async () => await ClosingCount(db) == 1);
            var closed = await db.HojasRuta.AsNoTracking().SingleAsync();
            Assert.Equal(EstadoHojaRuta.Finalizada, closed.Estado);
            Assert.Equal(cutoff, closed.FinEnUtc);
            Assert.False(closed.ReservaRecursosActiva);
            Assert.Equal(3, closed.UltimaSecuencia);
            var parcel = await db.Bultos.SingleAsync();
            Assert.Equal(EstadoCustodiaBulto.Repartidor, parcel.Custodia);
            Assert.Equal(EstadoEnvio.EnTransito, (await db.Envios.SingleAsync(e => e.Id == parcel.EnvioId)).Estado);
            var occupation = await db.OcupacionesRuta.SingleAsync();
            Assert.Null(occupation.RetornoEnUtc); Assert.Null(occupation.LiberadaEnUtc);
            var history = (await client.GetFromJsonAsync<HojaRutaResponse[]>($"/api/mobile/rutas?fecha={route.Fecha:yyyy-MM-dd}"))!;
            Assert.Single(Assert.Single(history).IncidenciasCierre);
            Assert.Equal("Conflicto", (await Aplicar(client, route, Operacion(Guid.NewGuid(), 4, "EntregaFisica", parcel.EnvioId, route.Paradas[0].EnvioVersion))).Resultado);
            clock.Advance(TimeSpan.FromDays(2));
            Assert.Equal(1, await ClosingCount(db));
            // Reception uses the automatic closing incident, preserving the same custody checks.
            await using var tx = await db.Database.BeginTransactionAsync();
            var repository = new EfRutasRepository(db, TimeProvider.System);
            Assert.True(await repository.RecibirAsync(closed.OperadorId, parcel.EnvioId, (await db.Envios.AsNoTracking().SingleAsync(e => e.Id == parcel.EnvioId)).Version, default));
            await tx.CommitAsync();
            Assert.Equal(EstadoCustodiaBulto.Deposito, (await db.Bultos.AsNoTracking().SingleAsync()).Custodia);
        }
        finally { await first.StopAsync(default); await second.StopAsync(default); }
    }

    [Fact]
    public async Task HostedDailyClosingRecoversAfterRollbackAndRestartAndKeepsPendingEvidence()
    {
        var data = await postgres.CreateDatabaseAsync();
        var apiClock = new ManualClock(DateTimeOffset.UtcNow);
        using var api = new IntegrationApiFactory(data, settings: new Dictionary<string,string?>
        { ["Reparto:EvidenciasEncryptionKey"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) }, timeProvider: apiClock);
        using var client = api.CreateHttpClient();
        var route = await Cargada(data, client);
        var parcel = route.Paradas[0];
        Assert.Equal("Aplicada", (await Aplicar(client, route, Operacion(Guid.NewGuid(), 4, "EntregaFisica", parcel.EnvioId, parcel.EnvioVersion))).Resultado);
        await using var db = data.CreateDbContext();
        var shipment = await db.Envios.AsNoTracking().SingleAsync(e => e.Id == parcel.EnvioId);
        var cutoff = new DateTimeOffset(route.Fecha.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(-3)).ToUniversalTime();
        var clock = new ClosingClock(cutoff.AddSeconds(-30));
        var connection = await CrearConexionWorker(data);
        var failure = new FailClosingSave();
        await using (var services = ClosingServices(connection, failure))
        using (var worker = ClosingWorker(services, clock))
        {
            await worker.StartAsync(default);
            await WaitClosing(() => Task.FromResult(clock.ActiveTimers > 0));
            clock.Advance(TimeSpan.FromSeconds(30));
            await WaitClosing(() => Task.FromResult(failure.Failed));
            await worker.StopAsync(default);
        }
        Assert.Equal(0, await ClosingCount(db));
        Assert.Equal(EstadoHojaRuta.EnCurso, (await db.HojasRuta.AsNoTracking().SingleAsync()).Estado);
        clock.Advance(TimeSpan.FromHours(1));
        await using (var services = ClosingServices(connection))
        using (var successor = ClosingWorker(services, clock))
        {
            await successor.StartAsync(default);
            await WaitClosing(async () => await ClosingCount(db) == 1);
            await successor.StopAsync(default);
        }
        var persisted = await db.Envios.AsNoTracking().SingleAsync(e => e.Id == parcel.EnvioId);
        Assert.Equal(EstadoEnvio.EntregaPendienteValidacion, persisted.Estado);
        Assert.Equal(shipment.PlazoPruebaVenceEnUtc, persisted.PlazoPruebaVenceEnUtc);
        Assert.Equal(shipment.Version, persisted.Version);
        Assert.Equal(4, (await db.HojasRuta.AsNoTracking().SingleAsync()).UltimaSecuencia);
        var foto = await SubirFoto(client, route, parcel.EnvioId);
        // No worker sequence was inserted: evidence remains admissible at the next mobile sequence.
        Assert.Equal("Aplicada", (await Aplicar(client, route, Operacion(Guid.NewGuid(), 5, "EvidenciaEntrega", parcel.EnvioId, persisted.Version,
            new { Nombre = "Synthetic recipient", Documento = "SYNTHETIC", FotoId = foto }))).Resultado);
        Assert.Equal(1, await ClosingCount(db));
    }

    [Theory]
    [InlineData("Planificada")]
    [InlineData("Despachada")]
    [InlineData("Cancelada")]
    [InlineData("Finalizada")]
    [InlineData("Futura")]
    public async Task HostedDailyClosingSkipsRoutesWithoutAnOpenJourney(string state)
    {
        var data = await postgres.CreateDatabaseAsync();
        using var api = data.CreateApiFactory(); using var client = api.CreateHttpClient();
        var route = await Cargada(data, client);
        await using var db = data.CreateDbContext();
        if (state == "Futura") await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE public."HojasRuta" SET "Fecha"="Fecha"+10 WHERE "Id"={route.Id} """);
        else await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE public."HojasRuta" SET "Estado"={state} WHERE "Id"={route.Id} """);
        var clock = new ClosingClock(DateTimeOffset.UtcNow.AddDays(3));
        await using var services = ClosingServices(await CrearConexionWorker(data));
        using var worker = ClosingWorker(services, clock);
        await worker.StartAsync(default);
        await WaitClosing(() => Task.FromResult(clock.ActiveTimers > 0));
        await worker.StopAsync(default);
        Assert.Equal(0, await ClosingCount(db));
        Assert.Equal(state == "Futura" ? "EnCurso" : state, (await db.HojasRuta.AsNoTracking().SingleAsync()).Estado.ToString());
    }

    private static ServiceProvider ClosingServices(string connection, params IInterceptor[] interceptors) =>
        new ServiceCollection().AddScoped<DailyClosingExecutor>().AddDbContext<NexoRutaDbContext>(o =>
            o.UseNexoRutaPostgres(connection).AddInterceptors(interceptors)).BuildServiceProvider();
    private static DailyClosingWorker ClosingWorker(IServiceProvider services, TimeProvider clock) => new(
        services.GetRequiredService<IServiceScopeFactory>(), clock,
        Options.Create(new DailyClosingOptions { Enabled=true, CutoffLocalTime=TimeOnly.MinValue, OfflinePolicy="RejectNewPhysicalOperations" }),
        NullLogger<DailyClosingWorker>.Instance);
    private static Task<int> ClosingCount(NexoRutaDbContext db) => db.Database.SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM public."CierresDiarios" WHERE "Resultado"='Aplicada' """).SingleAsync();
    private static async Task WaitClosing(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!await condition()) await Task.Delay(30, timeout.Token);
    }
    private sealed class FailClosingSave : SaveChangesInterceptor
    {
        public volatile bool Failed;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct=default)
        { Failed=true; throw new InvalidOperationException("Synthetic crash before closing commit"); }
    }
    private sealed class ClosingClock(DateTimeOffset initial) : TimeProvider
    {
        private readonly object gate = new(); private DateTimeOffset now=initial;
        private readonly List<ClosingTimer> timers=[];
        public int ActiveTimers { get { lock(gate) return timers.Count(t=>!t.Disposed); } }
        public override DateTimeOffset GetUtcNow() { lock(gate) return now; }
        public void Advance(TimeSpan delta)
        {
            ClosingTimer[] due;
            lock(gate) { now+=delta; due=timers.Where(t=>!t.Disposed && t.Due<=now).ToArray(); foreach(var timer in due) timer.Disposed=true; }
            foreach(var timer in due) timer.Callback(timer.State);
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { lock(gate) { var timer=new ClosingTimer(this,callback,state,now+dueTime); timers.Add(timer); return timer; } }
        private sealed class ClosingTimer(ClosingClock clock,TimerCallback callback,object? state,DateTimeOffset due) : ITimer
        {
            public TimerCallback Callback=>callback; public object? State=>state; public DateTimeOffset Due=due; public bool Disposed;
            public bool Change(TimeSpan dueTime,TimeSpan period) { lock(clock.gate) { Due=clock.now+dueTime; Disposed=false; return true; } }
            public void Dispose() { lock(clock.gate) Disposed=true; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
