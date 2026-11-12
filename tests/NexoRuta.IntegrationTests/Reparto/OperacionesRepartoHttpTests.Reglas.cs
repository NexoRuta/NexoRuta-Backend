using System.Net;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.ReglasOperativas;
using NexoRuta.Infrastructure.Administracion;
using NexoRuta.Infrastructure.ReglasOperativas;
using NexoRuta.IntegrationTests.Seguridad;
using static NexoRuta.IntegrationTests.Fixtures.RepartoHttpData;

namespace NexoRuta.IntegrationTests.Reparto;

public sealed partial class OperacionesRepartoHttpTests
{
    [Fact]
    public async Task PublicarOtrasReglasNoCambiaMotivosIntervaloNiMaximoDelEnvioDescargado()
    {
        var data = await postgres.CreateDatabaseAsync(); using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var hoja = await Cargada(data, client); var parada = hoja.Paradas[0];
        await PublicarOtraPolitica(data);
        var remota = (await client.GetFromJsonAsync<HojaRutaResponse>($"/api/mobile/rutas/{hoja.Id}"))!;
        Assert.Equal(parada.Reglas.VersionId, remota.Paradas[0].Reglas.VersionId);
        Assert.Equal(3, remota.Paradas[0].Reglas.MaximoIntentos);
        Assert.Equal("AUSENTE", Assert.Single(remota.Paradas[0].Reglas.Motivos).Codigo);
        long? version = parada.EnvioVersion;
        async Task<ResultadoOperacionResponse> Intentar(long secuencia, int segundos, string motivo = "AUSENTE")
        {
            var respuesta = await Aplicar(client, hoja, new { Id = Guid.NewGuid(), Secuencia = secuencia, Tipo = "IntentoFallido",
                EnvioId = parada.EnvioId, VersionEnvio = version, OcurridaEn = LlegadaEstimada.AddSeconds(segundos), Datos = new { Motivo = motivo } });
            version = respuesta.VersionEnvio; return respuesta;
        }
        Assert.Equal("Conflicto", (await Intentar(4, 0, "Ausente")).Resultado);
        Assert.Equal("Aplicada", (await Intentar(5, 0)).Resultado);
        Assert.Equal("Conflicto", (await Intentar(6, 30)).Resultado);
        Assert.Equal("Conflicto", (await Intentar(7, 60)).Resultado);
        Assert.Equal("Conflicto", (await Intentar(8, 120)).Resultado);
        Assert.Equal("Conflicto", (await Intentar(9, 180)).Resultado);
        await using var db = data.CreateDbContext();
        Assert.Equal(EstadoEnvio.NoEntregado, (await db.Envios.SingleAsync(e => e.Id == parada.EnvioId)).Estado);
        Assert.Equal(1, await db.OperacionesJornada.CountAsync(o => o.Tipo == "IntentoFallido" && o.Resultado == "Aplicada"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnviosNuevosUsanLaPublicacionParaValidarPruebaOIniciarDevolucion(bool intentoFallido)
    {
        var data = await postgres.CreateDatabaseAsync(); using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        await PublicarOtraPolitica(data);
        var hoja = await Cargada(data, client); var parada = hoja.Paradas[0];
        Assert.Equal(1, parada.Reglas.MaximoIntentos); Assert.True(parada.Reglas.RequiereNombre); Assert.False(parada.Reglas.RequiereFoto);
        var respuesta = await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 4, intentoFallido ? "IntentoFallido" : "EntregaFisica",
            parada.EnvioId, parada.EnvioVersion,
            new { Motivo = "RECHAZADO", Nombre = "Receptor de prueba", Latitud = -34.9, Longitud = -56.2,
                PrecisionMetros = 10, PosicionEnUtc = DateTimeOffset.UtcNow }));
        Assert.Equal("Aplicada", respuesta.Resultado);
        await using (var db = data.CreateDbContext())
        {
            Assert.Equal(intentoFallido ? EstadoEnvio.EnDevolucion : EstadoEnvio.Entregado,
                (await db.Envios.SingleAsync(e => e.Id == parada.EnvioId)).Estado);
            Assert.Equal(intentoFallido ? EstadoCustodiaBulto.Repartidor : EstadoCustodiaBulto.Destinatario,
                (await db.Bultos.SingleAsync(b => b.EnvioId == parada.EnvioId)).Custodia);
        }
        if (intentoFallido)
        {
            Assert.Equal("Aplicada", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 5, "Rendicion",
                datos: new { BultoIds = parada.Bultos.Select(b => b.Id).ToArray() }))).Resultado);
            using var recepcion = await client.PostAsJsonAsync($"/api/deposito/envios/{parada.EnvioId}/recepcion", new { Version = respuesta.VersionEnvio });
            Assert.Equal(System.Net.HttpStatusCode.NoContent, recepcion.StatusCode);
            await using var db = data.CreateDbContext();
            Assert.Equal(EstadoEnvio.Devuelto, (await db.Envios.SingleAsync(e => e.Id == parada.EnvioId)).Estado);
        }
    }

    [Fact]
    public async Task AP04TransfiereAlComercioUnBultoDevueltoQuePermaneceEnDeposito()
    {
        var data = await postgres.CreateDatabaseAsync();
        var browserOutput = Environment.GetEnvironmentVariable("NEXORUTA_AP04_BROWSER_DIR");
        var browserEnabled = !string.IsNullOrWhiteSpace(browserOutput);
        if (browserEnabled)
        {
            await using var seed = data.CreateDbContext();
            await AuthenticationTestData.Seeder(seed,
                new AccesoInicial("ap04-browser-commerce@example.test", "AP-04 operator", "AP-04 commerce", "ap04-browser-operator@example.test"))
                .SeedAsync();
            await ReglasEnvioTestData.SembrarInicialesAsync(seed);
            await ServiciosEnvioTestData.SembrarAsync(seed);
        }
        using var factory = browserEnabled
            ? new IntegrationApiFactory(data, settings: RlsRestrictedHttpFlowTests.ApiSettings(), seedOperationalRules: false)
            : data.CreateApiFactory();
        if (browserEnabled) factory.UseKestrel(0);
        using var operatorClient = string.IsNullOrWhiteSpace(browserOutput) ? factory.CreateHttpClient() : factory.CreateClient();
        await PublicarOtraPolitica(data);

        var route = await Cargada(data, operatorClient);
        var stop = Assert.Single(route.Paradas);
        var failedAttempt = await Aplicar(operatorClient, route, Operacion(Guid.NewGuid(), 4, "IntentoFallido",
            stop.EnvioId, stop.EnvioVersion, new
            {
                Motivo = "RECHAZADO", Nombre = "Destinatario sintético", Latitud = -34.9, Longitud = -56.2,
                PrecisionMetros = 10, PosicionEnUtc = DateTimeOffset.UtcNow
            }));
        Assert.Equal("Aplicada", failedAttempt.Resultado);
        Assert.Equal("Aplicada", (await Aplicar(operatorClient, route, Operacion(Guid.NewGuid(), 5, "Rendicion",
            datos: new { BultoIds = stop.Bultos.Select(parcel => parcel.Id).ToArray() }))).Resultado);
        using (var reception = await operatorClient.PostAsJsonAsync($"/api/deposito/envios/{stop.EnvioId}/recepcion",
            new { Version = failedAttempt.VersionEnvio }))
            Assert.Equal(HttpStatusCode.NoContent, reception.StatusCode);

        Guid parcelId;
        Guid commerceId;
        Guid operatorId;
        long shipmentVersionBeforeTransfer;
        long parcelVersionBeforeTransfer;
        EstadoEnvio shipmentStateBeforeTransfer;
        DateTimeOffset createdAt;
        string commerceEmail;
        string fixtureReference;
        await using (var beforeTransfer = data.CreateDbContext())
        {
            var shipment = await beforeTransfer.Envios.AsNoTracking().SingleAsync(item => item.Id == stop.EnvioId);
            var parcel = await beforeTransfer.Bultos.AsNoTracking().SingleAsync(item => item.EnvioId == stop.EnvioId);
            Assert.Equal(EstadoEnvio.Devuelto, shipment.Estado);
            Assert.Equal(ResultadoBulto.Devuelto, parcel.Resultado);
            Assert.Equal(EstadoCustodiaBulto.Deposito, parcel.Custodia);
            parcelId = parcel.Id;
            commerceId = shipment.ComercioId;
            operatorId = shipment.OperadorId;
            shipmentVersionBeforeTransfer = shipment.Version;
            parcelVersionBeforeTransfer = parcel.Version;
            shipmentStateBeforeTransfer = shipment.Estado;
            createdAt = shipment.CreadoEnUtc;
        }

        var commandId = Guid.NewGuid();
        using (var transfer = await operatorClient.PostAsJsonAsync($"/api/devoluciones/envios/{stop.EnvioId}/transferencias",
            new
            {
                ComandoId = commandId, VersionEnvio = shipmentVersionBeforeTransfer,
                ConfirmarTransferenciaFisica = true,
                Bultos = new[] { new { BultoId = parcelId, VersionBulto = parcelVersionBeforeTransfer } }
            }))
        {
            Assert.Equal(HttpStatusCode.OK, transfer.StatusCode);
            Assert.Equal(commandId, (await transfer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("transferenciaId").GetGuid());
        }

        await using (var afterTransfer = data.CreateDbContext())
        {
            var shipment = await afterTransfer.Envios.AsNoTracking().SingleAsync(item => item.Id == stop.EnvioId);
            var parcel = await afterTransfer.Bultos.AsNoTracking().SingleAsync(item => item.Id == parcelId);
            Assert.Equal(shipmentStateBeforeTransfer, shipment.Estado);
            Assert.Equal(ResultadoBulto.Devuelto, parcel.Resultado);
            Assert.Equal(EstadoCustodiaBulto.Comercio, parcel.Custodia);
            Assert.Equal(parcelVersionBeforeTransfer + 1, parcel.Version);

            var commerceAccess = await afterTransfer.AccesosUsuario.AsNoTracking()
                .SingleAsync(access => access.ComercioId == commerceId);
            using var commerceClient = string.IsNullOrWhiteSpace(browserOutput) ? factory.CreateHttpClient() : factory.CreateClient();
            await commerceClient.SetBearerForAccessAsync(data, commerceAccess.Id);
            var from = createdAt.AddDays(-1).ToString("O");
            var to = DateTimeOffset.UtcNow.AddDays(1).ToString("O");
            using var list = await commerceClient.GetAsync($"/api/devoluciones?operadorId={operatorId:D}&desdeUtc={Uri.EscapeDataString(from)}&hastaUtc={Uri.EscapeDataString(to)}&limite=50");
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            var item = Assert.Single((await list.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray(),
                row => row.GetProperty("envioId").GetGuid() == stop.EnvioId);
            Assert.Equal("PendienteAcuse", item.GetProperty("situacion").GetString());
            fixtureReference = item.GetProperty("referencia").GetString()
                ?? throw new InvalidOperationException("AP-04 test shipment must have a Commerce reference.");
            commerceEmail = await afterTransfer.Usuarios.AsNoTracking()
                .Where(user => user.Id == commerceAccess.UsuarioId).Select(user => user.Email).SingleAsync();

            using var detail = await commerceClient.GetAsync($"/api/devoluciones/envios/{stop.EnvioId:D}?operadorId={operatorId:D}");
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            var returnedParcel = Assert.Single((await detail.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("bultos").EnumerateArray());
            Assert.Equal("Devuelto", returnedParcel.GetProperty("resultado").GetString());
            Assert.Equal("Comercio", returnedParcel.GetProperty("custodia").GetString());
            Assert.True(returnedParcel.GetProperty("puedeAcusar").GetBoolean());
        }

        if (!string.IsNullOrWhiteSpace(browserOutput))
            await RunAp04BrowserAsync(operatorClient, browserOutput, commerceEmail, fixtureReference);
    }

    private static async Task RunAp04BrowserAsync(HttpClient api, string outputRoot, string commerceEmail, string reference)
    {
        var root = CompanionPortals.WorkspaceRoot;
        var output = Path.Combine(Path.GetFullPath(outputRoot), "run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var portalDirectory = Path.Combine(root, "frontend", "src", "NexoRuta.Commerce");
        var portalAssembly = Path.Combine(portalDirectory, "bin", CompanionPortals.Configuration, "net10.0", "NexoRuta.Commerce.dll");
        Assert.True(File.Exists(portalAssembly), "Build the exact Commerce companion before the AP-04 browser acceptance test.");

        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var commerceUrl = $"http://127.0.0.1:{port}";
        var portalStart = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = portalDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        portalStart.ArgumentList.Add(portalAssembly);
        portalStart.Environment["ASPNETCORE_URLS"] = commerceUrl;
        portalStart.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        portalStart.Environment["Api__BaseAddress"] = api.BaseAddress!.ToString();
        portalStart.Environment["OpenTelemetry__Enabled"] = "false";
        portalStart.Environment["DataProtection__KeysDirectory"] = Path.Combine(Path.GetTempPath(), "nexoruta-ap04-keys-" + Guid.NewGuid().ToString("N"));

        using var portal = Process.Start(portalStart) ?? throw new InvalidOperationException("Could not start Commerce for AP-04 browser acceptance.");
        var portalStdout = portal.StandardOutput.ReadToEndAsync();
        var portalStderr = portal.StandardError.ReadToEndAsync();
        try
        {
            using var health = new HttpClient();
            health.Timeout = TimeSpan.FromSeconds(3);
            var ready = false;
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try { ready = (await health.GetAsync(commerceUrl + "/health/ready")).IsSuccessStatusCode; }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
                if (ready) break;
                if (portal.HasExited) break;
                await Task.Delay(100);
            }
            if (!ready)
                throw new InvalidOperationException("Commerce did not become ready for AP-04 browser acceptance. " + await portalStdout + await portalStderr);

            var nodeStart = new ProcessStartInfo("node")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            nodeStart.ArgumentList.Add(Path.Combine(root, "backend", "tests", "browser", "ap04.cjs"));
            nodeStart.Environment["NEXORUTA_PLAYWRIGHT_MODULE"] = Environment.GetEnvironmentVariable("NEXORUTA_PLAYWRIGHT_MODULE")
                ?? Path.Combine(root, "backend", "tests", "browser", "node_modules", "playwright");
            nodeStart.Environment["AP04_BROWSER_BASE_URL"] = commerceUrl;
            nodeStart.Environment["AP04_API_BASE_URL"] = api.BaseAddress.ToString();
            nodeStart.Environment["AP04_BROWSER_EMAIL"] = commerceEmail;
            nodeStart.Environment["AP04_BROWSER_PASSWORD"] = AuthenticationTestData.Password;
            nodeStart.Environment["AP04_FIXTURE_REFERENCE"] = reference;
            nodeStart.Environment["AP04_EXPECT_PENDING"] = "true";
            nodeStart.Environment["AP04_VERIFY_PRIOR_FIXTURE"] = "false";
            nodeStart.Environment["AP04_FAULT_INJECTION"] = "false";
            nodeStart.Environment["AP04_EVIDENCE_DIR"] = output;
            using var node = Process.Start(nodeStart) ?? throw new InvalidOperationException("Could not start the AP-04 browser harness.");
            var stdout = node.StandardOutput.ReadToEndAsync();
            var stderr = node.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            try { await node.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!node.HasExited) node.Kill(entireProcessTree: true);
                throw new TimeoutException("AP-04 browser acceptance exceeded four minutes.");
            }
            Assert.True(node.ExitCode == 0, await stdout + await stderr);
        }
        finally
        {
            if (!portal.HasExited) portal.Kill(entireProcessTree: true);
            await portal.WaitForExitAsync();
            await Task.WhenAll(portalStdout, portalStderr);
        }
    }

    private static async Task PublicarOtraPolitica(IntegrationDatabase data)
    {
        await using var db = data.CreateDbContext();
        var operador = await db.AccesosUsuario.SingleAsync(a => a.OperadorId != null);
        var repo = new EfReglasOperativasRepository(db);
        await repo.CrearBorradorAsync(operador.OperadorId!.Value, operador.UsuarioId);
        await repo.EditarBorradorAsync(operador.OperadorId.Value, operador.UsuarioId, 0,
            new(1, 30, PoliticaDevolucionAlAgotarIntentos.IniciarAutomaticamente, 2880, 1440, 60,
                [TipoPruebaEntrega.NombreReceptor], [new("AUSENTE", "Ausente", false), new("RECHAZADO", "Rechazado", true)]));
        await repo.PublicarBorradorAsync(operador.OperadorId.Value, operador.UsuarioId, 1);
    }
}
