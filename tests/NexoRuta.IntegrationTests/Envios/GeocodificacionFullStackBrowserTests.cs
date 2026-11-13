using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexoRuta.Infrastructure.Envios.Geocodificacion;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;

namespace NexoRuta.IntegrationTests.Envios;

public sealed class GeocodificacionFullStackBrowserTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [FullStackFact]
    public Task CommerceAuthenticatedApiAndRestrictedPostgresUseControlledProviderOnly() => EjecutarAsync(false);

    [FullStackFact]
    public Task AdmissionSelectsAdjustsQuotesSavesAndReopensBeforeShipping() => EjecutarAsync(true);

    private async Task EjecutarAsync(bool alta)
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var seed = data.CreateShippingApiFactory()) using (seed.CreateHttpClient()) { }
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        var settings = RlsRestrictedHttpFlowTests.ApiSettings();
        // Only this isolated fixture traverses the adapter; its transport cannot reach any network.
        settings["Geocoding:MapboxTemporal:Habilitado"] = "true";
        settings["Mapbox:PublicAccessToken"] = "pk.synthetic-not-a-provider-credential";
        settings["Geocoding:Consumo:MaxSolicitudes24Horas"] = "3";
        settings["Geocoding:Consumo:SolicitudesPorMinutoUsuario"] = "60";
        var provider = new ControlledProvider(); var queries = new ConcurrentQueue<Query>();
        using var factory = new IntegrationApiFactory(runtime, settings: settings, configureTestServices: s =>
        {
            s.AddHttpClient(GeocodificadorMapboxTemporal.ClienteHttp).ConfigurePrimaryHttpMessageHandler(() => provider);
            s.AddSingleton<IStartupFilter>(new Capture(queries));
        });
        factory.UseKestrel(0);
        using var api = factory.CreateClient();
        await using var db = data.CreateDbContext();
        var commerce = await db.AccesosUsuario.SingleAsync(x => x.ComercioId != null);
        var email = await db.Usuarios.Where(x => x.Id == commerce.UsuarioId).Select(x => x.Email).SingleAsync();
        var op = await db.Operadores.SingleAsync();
        using (var anonymous = await api.PostAsJsonAsync($"/api/envios/{Guid.NewGuid()}/destino/busqueda", new { Consulta = "Primera" }))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        await api.SetBearerForAccessAsync(data, commerce.Id);
        var shipment = Guid.Empty;
        if (!alta)
        {
            using var created = await api.PostAsJsonAsync("/api/envios", new
        {
            OperadorId = op.Id, Servicio = NexoRuta.IntegrationTests.Fixtures.ServiciosEnvioTestData.Declarar(op.Id), DestinatarioNombre = "Synthetic geocoding recipient", DestinatarioEmail = "geo@example.test",
            Direccion = "Geocoding synthetic 123", Bultos = new List<NexoRuta.Api.Features.Envios.Contracts.Requests.BultoDeclaradoRequest> { new NexoRuta.Api.Features.Envios.Contracts.Requests.BultoDeclaradoRequest { Codigo = "GEO-FULLSTACK", PesoGramos = 100, LargoCentimetros = 10, AnchoCentimetros = 10, AltoCentimetros = 10 } }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            shipment = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        }
        var workspace = CompanionPortals.WorkspaceRoot;
        var contentRoot = Path.Combine(workspace, "frontend/src/NexoRuta.Commerce");
        var dll = Path.Combine(contentRoot, "bin", CompanionPortals.Configuration, "net10.0", "NexoRuta.Commerce.dll");
        Assert.True(File.Exists(dll), "Build current Commerce before the opt-in full-stack check.");
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var address = $"http://127.0.0.1:{port}/";
        var scratch = Path.Combine(Path.GetTempPath(), "nexoruta-geo-fullstack-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = contentRoot, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(dll);
        start.Environment["ASPNETCORE_URLS"] = address;
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["Api__BaseAddress"] = api.BaseAddress!.ToString();
        start.Environment["OpenTelemetry__Enabled"] = "false";
        start.Environment["Mapbox__PublicAccessToken"] = "pk.synthetic-not-a-credential";
        start.Environment["DataProtection__KeysDirectory"] = Path.Combine(scratch, "keys");
        using var commerceHost = Process.Start(start)!;
        var stdout = commerceHost.StandardOutput.ReadToEndAsync(); var stderr = commerceHost.StandardError.ReadToEndAsync();
        var outputDirectory = Environment.GetEnvironmentVariable("NEXORUTA_GEOCODING_FULLSTACK_DIR")!;
        Directory.CreateDirectory(outputDirectory);
        try
        {
            using var probe = new HttpClient(); var ready = false;
            for (var i = 0; i < 150; i++)
            {
                try { if ((await probe.GetAsync(address + "ready")).IsSuccessStatusCode) { ready = true; break; } }
                catch (HttpRequestException) { }
                if (commerceHost.HasExited) break;
                await Task.Delay(100);
            }
            Assert.True(ready, "Isolated Commerce failed readiness.");
            var nodeStart = new ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            nodeStart.ArgumentList.Add(Path.Combine(workspace, alta ? "frontend/tests/browser/admission.cjs" : "frontend/tests/browser/destination-geocoding.cjs"));
            nodeStart.Environment["DESTINATION_BASE_URL"] = address;
            nodeStart.Environment["DESTINATION_EMAIL"] = email;
            nodeStart.Environment["DESTINATION_PASSWORD"] = AuthenticationTestData.Password;
            nodeStart.Environment["DESTINATION_SHIPMENT"] = shipment.ToString();
            nodeStart.Environment["ADMISSION_OPERATOR"] = op.Id.ToString();
            nodeStart.Environment["DESTINATION_FULLSTACK"] = "true";
            nodeStart.Environment["NEXORUTA_DESTINATION_BROWSER_DIR"] = outputDirectory;
            File.Delete(Path.Combine(outputDirectory,"conflict.ready"));File.Delete(Path.Combine(outputDirectory,"conflict.applied"));
            using var node = Process.Start(nodeStart)!;
            var output = node.StandardOutput.ReadToEndAsync(); var error = node.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try {
                if (!alta)
                {
                while(!File.Exists(Path.Combine(outputDirectory,"conflict.ready"))) {
                    if (node.HasExited) Assert.Fail("Browser exited before concurrency checkpoint: " + await output + await error);
                    await Task.Delay(50,timeout.Token);
                }
                using var concurrent=await api.PutAsJsonAsync($"/api/envios/{shipment}/destino",new {
                    Version=3,Latitud=-34.7,Longitud=-56.0,Direccion="Concurrente 123"},timeout.Token);
                Assert.Equal(HttpStatusCode.NoContent,concurrent.StatusCode);
                await File.WriteAllTextAsync(Path.Combine(outputDirectory,"conflict.applied"),"applied",timeout.Token);
                }
                await node.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) { node.Kill(true); throw; }
            Assert.True(node.ExitCode == 0, await output + await error);
            db.ChangeTracker.Clear();
            if (alta)
            {
                Assert.Single(await db.Envios.ToArrayAsync());
                Assert.Equal(2, await db.Bultos.CountAsync());
                var saved = await db.Direcciones.SingleAsync();
                Assert.Equal("Primera 123", saved.Descripcion); Assert.Equal(-34.91, saved.Latitud); Assert.Equal(-56.21, saved.Longitud);
                Assert.NotNull(saved.ConfirmadaEnUtc);
                Assert.Equal(new[] { "Primera" }, provider.Queries.ToArray());
                Assert.Contains(queries, q => q.Path == "/api/envios/destino/busqueda" && q.Status == 200 && q.Authenticated);
                Assert.Contains(queries, q => q.Path == "/api/envios/cotizacion" && q.Status == 200 && q.Authenticated);
                Assert.Contains(queries, q => q.Path == "/api/envios" && q.Method == "POST" && q.Status == 201 && q.Authenticated);
                Assert.Equal(2, await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM public.\"TarifasEnvio\"").SingleAsync());
            }
            else
            {
            var saved = await db.Direcciones.SingleAsync();
            using var marker = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(outputDirectory, "real-marker-result.json")));
            var selected = marker.RootElement.GetProperty("finalPoint");
            Assert.InRange(Math.Abs(selected.GetProperty("lat").GetDouble() + 34.91), 0, 0.00001);
            Assert.InRange(Math.Abs(selected.GetProperty("lon").GetDouble() + 56.21), 0, 0.00001);
            Assert.Equal(6, saved.Version);
            Assert.Equal(selected.GetProperty("lat").GetDouble(), saved.Latitud);
            Assert.Equal(selected.GetProperty("lon").GetDouble(), saved.Longitud);
            Assert.Equal("Cambio 123", saved.Descripcion); Assert.Equal(commerce.UsuarioId, saved.ConfirmadaPorUsuarioId);
            Assert.Equal(3, await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM public.\"UsoGeocodificacion\"").SingleAsync());
            Assert.Equal(new[] { "Primera", "Cambio", "Fallo" }, provider.Queries.ToArray());
            Assert.Contains(queries, x => x.Path.EndsWith("/busqueda") && x.Status == 429 && x.Authenticated);
            Assert.Contains(queries, x => x.Path.EndsWith("/busqueda") && x.Status == 503 && x.Authenticated);
            Assert.Equal(5, queries.Count(x => x.Method == "PUT" && x.Status == 204 && x.Authenticated));
            Assert.Single(queries.Where(x=>x.Method=="PUT"&&x.Status==409&&x.Authenticated));
            }
        }
        finally
        {
            if (!commerceHost.HasExited) commerceHost.Kill(true);
            await commerceHost.WaitForExitAsync();
            Directory.CreateDirectory(scratch);
            await File.WriteAllTextAsync(Path.Combine(scratch, "commerce.log"), await stdout + await stderr);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "api-queries.json"), JsonSerializer.Serialize(queries, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Full-stack scratch (parent cleanup): " + scratch);
        }
    }

    private sealed class FullStackFact : FactAttribute
    { public FullStackFact() { if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NEXORUTA_GEOCODING_FULLSTACK_DIR"))) Skip = "Opt-in Commerce / authenticated real API / restricted PostgreSQL, controlled provider transport only."; } }
    private sealed record Query(string Method, string Path, int Status, bool Authenticated);
    private sealed class Capture(ConcurrentQueue<Query> queries) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (ctx, continuation) =>
            {
                await continuation(); var path = ctx.Request.Path.Value ?? "";
                if (path.StartsWith("/api/envios", StringComparison.Ordinal)) queries.Enqueue(new(ctx.Request.Method, path, ctx.Response.StatusCode, ctx.User.Identity?.IsAuthenticated == true));
            }); next(app);
        };
    }
    private sealed class ControlledProvider : HttpMessageHandler
    {
        public ConcurrentQueue<string> Queries { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("api.mapbox.com", request.RequestUri!.Host);
            Assert.Equal("/search/geocode/v6/forward", request.RequestUri.AbsolutePath);
            Assert.Contains("permanent=false", request.RequestUri.Query);
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query)["q"].ToString();
            Queries.Enqueue(query);
            return Task.FromResult(query == "Fallo"
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("CONTROLLED_PRIVATE_ERROR") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { features = new[] { new { geometry = new {coordinates = new[]{-56.2,-34.9}}, properties = new {full_address=query+" 123"} } } }) });
        }
    }
}
