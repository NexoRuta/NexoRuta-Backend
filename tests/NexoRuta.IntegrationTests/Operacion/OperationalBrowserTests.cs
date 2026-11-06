using System.Diagnostics;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NexoRuta.Infrastructure.Outbox;
using NexoRuta.Worker;
using NexoRuta.Worker.Messaging;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.Domain.Envios;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.Infrastructure.Seguimiento.Consultas;
using NexoRuta.Infrastructure.Seguimiento.Processing;
using NexoRuta.Infrastructure.Seguimiento.Repositories;
using NexoRuta.Infrastructure.Seguimiento.Security;
using NexoRuta.Application.Seguimiento.UseCases;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;
using static NexoRuta.IntegrationTests.Fixtures.RepartoHttpData;
namespace NexoRuta.IntegrationTests.Operacion;

[Collection(OperationalInfrastructureCollection.Name)]
public sealed class OperationalBrowserTests(PostgresFixture postgres, KafkaFixture kafka) : IClassFixture<PostgresFixture>, IClassFixture<KafkaFixture>
{
    [Fact]
    public async Task RealTrackingRequestsAndBackofficeUseRestrictedApiAndPreservePhysicalCustody()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4)); var ct = deadline.Token;
        var database = await postgres.CreateDatabaseAsync();
        using (var seed = database.CreateShippingApiFactory()) using (seed.CreateHttpClient()) { }
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(database);
        var settings = RlsRestrictedHttpFlowTests.ApiSettings(); settings["DatabaseInitialization:SeedData"] = "false";
        using var factory = new IntegrationApiFactory(runtime, settings: settings, seedOperationalRules: false); factory.UseKestrel(0);
        using var api = factory.CreateClient();
        using var projection = await StartProjection(database, ct);
        var ready = await Preparar(database, api, bloquearDiscrepanciasCarga: false);
        using var planned = await api.PostAsJsonAsync("/api/rutas", new { RepartidorAccesoId = ready.AccesoId, ready.VehiculoId, Fecha = FechaOperacion, Paradas = new[] { new { ready.EnvioId, LlegadaEstimadaUtc = DateTimeOffset.UtcNow.AddMinutes(10), ServicioMinutos = 5 } } }, ct);
        Assert.True(planned.IsSuccessStatusCode, await planned.Content.ReadAsStringAsync(ct));
        var route = (await planned.Content.ReadFromJsonAsync<HojaRutaResponse>(ct))!;
        using var dispatched = await api.PostAsJsonAsync($"/api/rutas/{route.Id}/despacho", new { route.Version }, ct);
        Assert.True(dispatched.IsSuccessStatusCode, await dispatched.Content.ReadAsStringAsync(ct));
        var stop = route.Paradas.Single();
        await Apply(1, "InicioJornada"); await Apply(2, "CargarBulto", stop.EnvioId, stop.EnvioVersion, new { BultoId = stop.Bultos[0].Id }); await Apply(3, "ConfirmarCarga");
        var loaded = (await api.GetFromJsonAsync<HojaRutaResponse>($"/api/mobile/rutas/{route.Id}", ct))!;
        var keyConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Seguimiento:EncryptionKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }).Build();
        Guid op; string email;
        await using (var db = database.CreateDbContext())
        {
            var shipment = await db.Envios.SingleAsync(x => x.Id == ready.EnvioId, ct); op = shipment.OperadorId;
            email = await (from a in db.AccesosUsuario join u in db.Usuarios on a.UsuarioId equals u.Id where a.Id == ready.AccesoId select u.Email).SingleAsync(ct);
        }
        var trackingJob = await PublishedTrackingJobTestData.CreatePublishedTrackingJobAsync(database, op, ready.EnvioId);
        string token;
        await using (var db = database.CreateDbContext())
        {
            await new ProcesarGeneracionSeguimiento(db, new CrearEnlaceSeguimientoUseCase(new EfConsultaEstadoEnvio(db), new EfEnlacesSeguimientoRepository(db)), new CifradorTokenSeguimiento(keyConfig)).EjecutarAsync(trackingJob, op, ready.EnvioId, ct);
            token = new CifradorTokenSeguimiento(keyConfig).Descifrar(await db.EntregasSeguimiento.SingleAsync(x => x.EnvioId == ready.EnvioId, ct));

        }
        // A manual arrival plus positive service duration is an estimate, never a promised slot.
        var caughtUp = false;
        for (var attempt = 0; attempt < 45; attempt++)
        {
            using var status = await api.GetAsync("/api/operacion/fotografia", ct);
            Assert.True(status.IsSuccessStatusCode, $"{status.StatusCode}: {await status.Content.ReadAsStringAsync(ct)}");
            var snapshot = await status.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (snapshot.GetProperty("estadoProyeccion").GetString() == "Actualizada") { caughtUp = true; break; }
            await Task.Delay(1000, ct);
        }
        Assert.True(caughtUp, "The private source frontier must catch up before checking the public estimate.");
        using var estimateResponse = await api.GetAsync($"/api/public/seguimiento/{token}", ct);
        Assert.True(estimateResponse.IsSuccessStatusCode, $"{estimateResponse.StatusCode}: {await estimateResponse.Content.ReadAsStringAsync(ct)}");
        var estimated = await estimateResponse.Content.ReadFromJsonAsync<JsonElement>(ct);
        var window = estimated.GetProperty("ventanaEstimada");
        Assert.Equal(JsonValueKind.Object, window.ValueKind);
        Assert.Contains("manual", window.GetProperty("origen").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(TimeSpan.FromMinutes(5), window.GetProperty("hastaUtc").GetDateTimeOffset() - window.GetProperty("desdeUtc").GetDateTimeOffset());
        await Apply(4, "IntentoFallido", stop.EnvioId, loaded.Paradas[0].EnvioVersion, new { Motivo = "AUSENTE" });
        await using (var check = runtime.CreateDbContext())
            Assert.NotNull(await new EfEnlacesSeguimientoRepository(check).ObtenerSeguimientoPublicoAsync(token, ct));
        using (var publicResponse = await api.GetAsync($"/api/public/seguimiento/{token}", ct))
        {
            Assert.True(publicResponse.IsSuccessStatusCode, await publicResponse.Content.ReadAsStringAsync(ct));
            var detail = await publicResponse.Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal("NoEntregado", detail.GetProperty("estado").GetString());
            Assert.Equal(JsonValueKind.Null, detail.GetProperty("ventanaEstimada").ValueKind);
        }
        await Apply(5, "Posicion", data: new { Latitud = -34.89, Longitud = -56.19, PrecisionMetros = 5, PosicionEnUtc = DateTimeOffset.UtcNow.AddSeconds(-1) });
        var root = FindRoot(); var temp = Directory.CreateTempSubdirectory("nexoruta-operational-browser-");
        var trackingUrl = $"http://127.0.0.1:{FreePort()}"; var backofficeUrl = $"http://127.0.0.1:{FreePort()}";
        using var tracking = StartPortal(root, "NexoRuta.Tracking", trackingUrl, api.BaseAddress!, Path.Combine(temp.FullName, "tracking-keys"));
        using var backoffice = StartPortal(root, "NexoRuta.Backoffice", backofficeUrl, api.BaseAddress!, Path.Combine(temp.FullName, "backoffice-keys"));
        try
        {
            await Task.WhenAll(WaitReady(tracking, trackingUrl, ct), WaitReady(backoffice, backofficeUrl, ct));
            var output = Environment.GetEnvironmentVariable("NEXORUTA_OPERATIONAL_E2E_DIR") ?? Path.Combine(root, "backend", "artifacts", "browser", "operational"); Directory.CreateDirectory(output);
            var start = new ProcessStartInfo("node") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(root, "frontend", "tests", "browser", "operational.cjs"));
            start.Environment["OPERATIONAL_TRACKING_URL"] = trackingUrl + "/seguimiento/" + token; start.Environment["OPERATIONAL_BACKOFFICE_URL"] = backofficeUrl;
            start.Environment["OPERATIONAL_EMAIL"] = email; start.Environment["OPERATIONAL_PASSWORD"] = AuthenticationTestData.Password; start.Environment["OPERATIONAL_OUTPUT"] = output;
            using var browser = Process.Start(start)!; var stdout = browser.StandardOutput.ReadToEndAsync(ct); var stderr = browser.StandardError.ReadToEndAsync(ct);
            await browser.WaitForExitAsync(ct); Assert.True(browser.ExitCode == 0, await stdout + await stderr);
            await using var verify = database.CreateDbContext();
            Assert.Equal(1, await verify.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM public.\"SolicitudesReprogramacionPublica\" WHERE \"EnvioId\"={ready.EnvioId} AND \"Estado\"='Tratada'").SingleAsync(ct));
            Assert.Equal(1,await verify.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value" FROM public."EnvioEventos" h JOIN public."SolicitudesReprogramacionPublica" r ON h."OrigenId"=r."Id"
                WHERE h."EnvioId"={ready.EnvioId} AND h."EstadoNuevo"='Reprogramado'
                """).SingleAsync(ct));
            Assert.Equal(EstadoEnvio.Reprogramado, (await verify.Envios.SingleAsync(x => x.Id == ready.EnvioId, ct)).Estado);
            Assert.All(await verify.Bultos.Where(x => x.EnvioId == ready.EnvioId).ToArrayAsync(ct), b => Assert.Equal(EstadoCustodiaBulto.Repartidor, b.Custodia));
        }
        finally { await projection.StopAsync(CancellationToken.None); Stop(tracking); Stop(backoffice); temp.Delete(true); }
        async Task Apply(long sequence, string type, Guid? shipment = null, long? version = null, object? data = null)
        {
            using var response = await api.PostAsJsonAsync($"/api/mobile/jornadas/{route.JornadaId}/operaciones", new { Id = Guid.NewGuid(), Secuencia = sequence, Tipo = type, EnvioId = shipment, VersionEnvio = version, OcurridaEn = DateTimeOffset.UtcNow, Datos = data ?? new { } }, ct);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(ct));
            Assert.Equal("Aplicada", (await response.Content.ReadFromJsonAsync<ResultadoOperacionResponse>(ct))!.Resultado);
        }
    }
    [Fact]
    public async Task AnonymousTrackingPaginatesRealParcelsAndKeepsKeyboardFocusAtMobileWidth()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3)); var ct = deadline.Token;
        var data = await postgres.CreateDatabaseAsync();
        string token;
        await using (var seed = data.CreateDbContext())
        {
            var shipment = await NexoRuta.IntegrationTests.Seguimiento.SeguimientoTestData.CreateEnvioAsync(seed);
            seed.Bultos.AddRange(Enumerable.Range(1,250).Select(i => new Bulto(shipment.OperadorId,shipment.Id,$"PRIVATE-PARCEL-{i}",1,1,1,1)));
            await seed.SaveChangesAsync(ct);
            token = await NexoRuta.IntegrationTests.Seguimiento.SeguimientoTestData.CreateActiveLinkAsync(seed,shipment);
        }
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        var settings = RlsRestrictedHttpFlowTests.ApiSettings(); settings["DatabaseInitialization:SeedData"]="false";
        using var factory = new IntegrationApiFactory(runtime,settings:settings,seedOperationalRules:false); factory.UseKestrel(0);
        using var api = factory.CreateClient();
        var root = FindRoot(); var temp = Directory.CreateTempSubdirectory("nexoruta-public-parcels-");
        var address=$"http://127.0.0.1:{FreePort()}";
        using var portal=StartPortal(root,"NexoRuta.Tracking",address,api.BaseAddress!,Path.Combine(temp.FullName,"keys"));
        try
        {
            await WaitReady(portal,address,ct);
            var output=Environment.GetEnvironmentVariable("NEXORUTA_OPERATIONAL_E2E_DIR") ?? Path.Combine(root,"backend","artifacts","browser","operational"); Directory.CreateDirectory(output);
            var start=new ProcessStartInfo("node") { UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true };
            start.ArgumentList.Add(Path.Combine(root,"frontend","tests","browser","public-parcels.cjs"));
            start.Environment["OPERATIONAL_TRACKING_URL"]=address+"/seguimiento/"+token;start.Environment["OPERATIONAL_OUTPUT"]=output;
            using var browser=Process.Start(start)!;var stdout=browser.StandardOutput.ReadToEndAsync(ct);var stderr=browser.StandardError.ReadToEndAsync(ct);
            await browser.WaitForExitAsync(ct);Assert.True(browser.ExitCode==0,await stdout+await stderr);
        }
        finally { Stop(portal);temp.Delete(true); }
    }

    private async Task<IHost> StartProjection(IntegrationDatabase data, CancellationToken ct)
    {
        var topic="e2e-operational-"+Guid.NewGuid().ToString("N");
        using var admin=new AdminClientBuilder(new AdminClientConfig { BootstrapServers=kafka.BootstrapServers }).Build();
        await admin.CreateTopicsAsync([
            new TopicSpecification { Name=topic,NumPartitions=2,ReplicationFactor=1,Configs=new Dictionary<string,string> { ["retention.ms"]="-1" } },
            new TopicSpecification { Name=CommerceNoticeConsumer.Topic,NumPartitions=3,ReplicationFactor=1 }
        ]);
        var builder=Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string,string?> {
            ["Kafka:BootstrapServers"]=kafka.BootstrapServers,["Operational:Topic"]=topic,["Operational:Partitions"]="2",
            ["Operational:nexoruta-operational-board-v1:Group"]="board-e2e-"+Guid.NewGuid().ToString("N")
        });
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<NexoRutaDbContext>(options=>options.UseNexoRutaPostgres(data.ConnectionString));
        builder.Services.AddScoped<EfOutboxRepository>();
        builder.Services.AddSingleton<KafkaEnvioCreadoPublisher>();
        builder.Services.AddSingleton<IOutboxMessageHandler,OperationalOutboxHandler>();
        builder.Services.AddSingleton<IOutboxMessageHandler,KafkaShipmentCreatedOutboxHandler>();
        builder.Services.AddSingleton<IOutboxMessageHandler>(sp=>new CommerceNoticeOutboxHandler(sp.GetRequiredService<KafkaEnvioCreadoPublisher>(),"BultoActualizado"));
        builder.Services.AddSingleton<IOutboxMessageHandler>(sp=>new CommerceNoticeOutboxHandler(sp.GetRequiredService<KafkaEnvioCreadoPublisher>(),"OperacionActualizada"));
        builder.Services.AddSingleton<OutboxHandlerRegistry>();
        builder.Services.AddHostedService<KafkaOutboxWorker>();
        builder.Services.AddHostedService<OperationalBoardConsumer>();
        var host=builder.Build();await host.StartAsync(ct);return host;
    }

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), Path.GetDirectoryName(source)! })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "frontend", "NexoRuta.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Build the pinned frontend companion beside backend.");
    }
    private static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    private static Process StartPortal(string root, string name, string address, Uri api, string keys)
    {
        var dll = Path.Combine(root, "frontend", "src", name, "bin", CompanionPortals.Configuration, "net10.0", name + ".dll"); Assert.True(File.Exists(dll), "Build the frontend companion before browser tests.");
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = Path.GetDirectoryName(dll)!, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(dll); start.ArgumentList.Add("--urls"); start.ArgumentList.Add(address); start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development"; start.Environment["Api__BaseAddress"] = api.AbsoluteUri; start.Environment["DataProtection__KeysDirectory"] = keys;
        var process = Process.Start(start)!; _ = process.StandardOutput.ReadToEndAsync(); _ = process.StandardError.ReadToEndAsync(); return process;
    }
    private static async Task WaitReady(Process process, string address, CancellationToken ct)
    { using var http = new HttpClient(); while (!ct.IsCancellationRequested && !process.HasExited) { try { if ((await http.GetAsync(address + "/health/ready", ct)).IsSuccessStatusCode) return; } catch (HttpRequestException) { } await Task.Delay(100, ct); } throw new InvalidOperationException("Isolated portal failed readiness."); }
    private static void Stop(Process process) { if (!process.HasExited) { process.Kill(true); process.WaitForExit(); } }
}
