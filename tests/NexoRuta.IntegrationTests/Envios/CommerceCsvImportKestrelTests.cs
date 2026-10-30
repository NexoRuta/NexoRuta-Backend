using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.ReglasOperativas;
using NexoRuta.Domain.Reparto;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.Infrastructure.Outbox;
using NexoRuta.Infrastructure.ReglasOperativas;
using Npgsql;

namespace NexoRuta.IntegrationTests.Envios;

public sealed class CommerceCsvImportKestrelTests(PostgresFixture postgres, Xunit.Abstractions.ITestOutputHelper output) : IClassFixture<PostgresFixture>
{
    private const long MaximumFileBytes = 5 * 1024 * 1024;
    private const string Header = "referenciaExterna,nombreDestinatario,emailDestinatario,direccion,codigoBulto,pesoGramos,largoCentimetros,anchoCentimetros,altoCentimetros";

    [Fact]
    public async Task KestrelAndCommerceBrowserUseIsolatedPostgresForImportAndReimport()
    {
        var database = await postgres.CreateDatabaseAsync();
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nexoruta-cu07-commerce-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        KestrelProcess? apiProcess = null;
        KestrelProcess? commerceProcess = null;
        ChromiumPage? browserProcess = null;
        try
        {
            const string commerceEmail = "cu07-kestrel-commerce@example.test";
            const string operatorEmail = "cu07-kestrel-operator@example.test";
            var seedSettings = AuthenticationTestData.Settings();
            seedSettings["AccesoInicial:UsuarioEmail"] = commerceEmail;
            seedSettings["AccesoInicial:OperadorUsuarioEmail"] = operatorEmail;
            seedSettings["DataProtection:KeysDirectory"] = Path.Combine(tempRoot, "api-keys");
            using (var seedFactory = new IntegrationApiFactory(database, settings: seedSettings))
            using (seedFactory.CreateHttpClient()) { }

            Guid operatorId;
            Guid commerceAccessId;
            await using (var db = database.CreateDbContext())
            {
                operatorId = await db.Operadores.Select(item => item.Id).SingleAsync();
                commerceAccessId = await (from access in db.AccesosUsuario
                                          join user in db.Usuarios on access.UsuarioId equals user.Id
                                          where user.Email == commerceEmail && access.ComercioId != null
                                          select access.Id).SingleAsync();
            }

            var runtimeConnection = await CreateRestrictedApiConnectionAsync(database);
            var apiPort = FindFreePort();
            var commercePort = FindFreePort();
            var apiUrl = $"http://127.0.0.1:{apiPort}";
            var commerceUrl = $"http://127.0.0.1:{commercePort}";
            var root = FindWorkspaceRoot();
            var apiDll = Path.Combine(root, "backend", "src", "NexoRuta.Api", "bin", "Debug", "net10.0", "NexoRuta.Api.dll");
            var commerceDll = Path.Combine(root, "frontend", "src", "NexoRuta.Commerce", "bin", "Debug", "net10.0", "NexoRuta.Commerce.dll");
            Assert.True(File.Exists(apiDll), "Build the API before the Kestrel verification.");
            Assert.True(File.Exists(commerceDll), "Build Commerce before the browser verification.");

            var api = StartDotnet(apiDll, Path.GetDirectoryName(apiDll)!, apiUrl, new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["ConnectionStrings__Postgres"] = runtimeConnection,
                ["DatabaseInitialization__ApplyMigrations"] = "false",
                ["DatabaseInitialization__SeedData"] = "false",
                ["OpenTelemetry__Enabled"] = "false",
                ["Jwt__Issuer"] = AuthenticationTestData.Issuer,
                ["Jwt__Audience"] = AuthenticationTestData.Audience,
                ["Jwt__SigningKeyBase64"] = AuthenticationTestData.SigningKey,
                ["DataProtection__KeysDirectory"] = Path.Combine(tempRoot, "api-keys"),
                ["Tracking__PublicBaseUrl"] = "https://tracking.example.test",
                ["PasswordRecovery__ResetPageUrl"] = "https://commerce.example.test/restablecer-contrasena",
                ["PasswordRecovery__TokenLifespanSeconds"] = "900",
                ["Email__Smtp__Host"] = "mailpit.integration.test",
                ["Email__Smtp__Port"] = "1025",
                ["Email__Smtp__UseSsl"] = "false",
                ["Email__Smtp__FromAddress"] = "noreply@example.test",
                ["Email__Smtp__FromName"] = "NexoRuta integration"
            });
            apiProcess = api;
            await WaitForHttpAsync(api.Process, $"{apiUrl}/health/live");

            var commerce = StartDotnet(commerceDll, Path.GetDirectoryName(commerceDll)!, commerceUrl,
                new Dictionary<string, string?>
                {
                    ["ASPNETCORE_ENVIRONMENT"] = "Development",
                    ["Api__BaseAddress"] = apiUrl + "/"
                });
            commerceProcess = commerce;
            await WaitForHttpAsync(commerce.Process, $"{commerceUrl}/health/ready");

            using var http = new HttpClient { BaseAddress = new Uri(apiUrl), Timeout = TimeSpan.FromSeconds(90) };
            using var missingAuth = await PostCsvAsync(http, operatorId, Csv(Row("NO-AUTH")));
            Assert.Equal(HttpStatusCode.Unauthorized, missingAuth.StatusCode);

            var bearer = await LoginAsync(http, commerceEmail, AuthenticationTestData.Password);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var operatorClient = new HttpClient { BaseAddress = new Uri(apiUrl) };
            var operatorBearer = await LoginAsync(operatorClient, operatorEmail, AuthenticationTestData.Password);
            operatorClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", operatorBearer);
            using (var denied = await PostCsvAsync(operatorClient, operatorId, Csv(Row("OPERATOR-DENIED"))))
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

            var beforeIndividual = await CountsAsync(database);
            using (var individual = await http.PostAsJsonAsync("/api/envios", new
            {
                OperadorId = operatorId,
                DestinatarioNombre = "Individual Kestrel recipient",
                DestinatarioEmail = "individual-kestrel@example.test",
                Direccion = "18 de Julio 123",
                CodigoBulto = "INDIVIDUAL-KST",
                PesoGramos = 500m,
                LargoCentimetros = 20m,
                AnchoCentimetros = 15m,
                AltoCentimetros = 10m
            }))
                Assert.Equal(HttpStatusCode.Created, individual.StatusCode);
            Assert.Equal((beforeIndividual.Shipments + 1, beforeIndividual.Outbox + 2), await CountsAsync(database));

            using (var created = await PostCsvAsync(http, operatorId, Csv(Row("KESTREL-NEW"))))
            {
                Assert.Equal(HttpStatusCode.OK, created.StatusCode);
                Assert.Equal("Creado", FirstState(await created.Content.ReadAsStringAsync()));
            }
            var outboxAfterFirst = await CountOutboxAsync(database);
            using (var fullRetry = await PostCsvAsync(http, operatorId, Csv(Row("KESTREL-NEW"))))
            {
                Assert.Equal(HttpStatusCode.OK, fullRetry.StatusCode);
                Assert.Equal("YaExistente", FirstState(await fullRetry.Content.ReadAsStringAsync()));
            }
            Assert.Equal(outboxAfterFirst, await CountOutboxAsync(database));

            using (var mixed = await PostCsvAsync(http, operatorId, Csv(Row("KESTREL-NEW"), Row("KESTREL-MIXED"))))
            {
                Assert.Equal(HttpStatusCode.OK, mixed.StatusCode);
                Assert.Equal(new[] { "YaExistente", "Creado" }, States(await mixed.Content.ReadAsStringAsync()));
            }
            using (var duplicates = await PostCsvAsync(http, operatorId, Csv(Row("KESTREL-DUP", weight: "10.0"), Row("KESTREL-DUP", weight: "10.00"))))
            {
                Assert.Equal(HttpStatusCode.OK, duplicates.StatusCode);
                Assert.Equal(new[] { "Creado", "DuplicadoDe" }, States(await duplicates.Content.ReadAsStringAsync()));
            }

            var bulkCsv = Csv(Enumerable.Range(1, 500).Select(index => Row($"KESTREL-BULK-{index:D3}")).ToArray());
            var bulkWatch = Stopwatch.StartNew();
            using (var bulk = await PostCsvAsync(http, operatorId, bulkCsv))
            {
                bulkWatch.Stop();
                Assert.Equal(HttpStatusCode.OK, bulk.StatusCode);
                Assert.Equal(500, States(await bulk.Content.ReadAsStringAsync()).Count(state => state == "Creado"));
            }
            output.WriteLine($"Kestrel 500-row CSV: {bulkWatch.Elapsed.TotalMilliseconds:F0} ms, isolated PostGIS Testcontainers PostgreSQL, one local request.");

            var overlapWatch = Stopwatch.StartNew();
            var overlap = await Task.WhenAll(
                PostCsvAsync(http, operatorId, Csv(Row("KESTREL-LOCK-A"), Row("KESTREL-LOCK-B"))),
                PostCsvAsync(http, operatorId, Csv(Row("KESTREL-LOCK-B"), Row("KESTREL-LOCK-A"))));
            overlapWatch.Stop();
            try
            {
                Assert.All(overlap, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
                var combinedStates = await Task.WhenAll(overlap.Select(async response => States(await response.Content.ReadAsStringAsync())));
                Assert.Equal(2, combinedStates.SelectMany(states => states).Count(state => state == "Creado"));
                Assert.Equal(2, combinedStates.SelectMany(states => states).Count(state => state == "YaExistente"));
            }
            finally
            {
                foreach (var response in overlap) response.Dispose();
            }
            output.WriteLine($"Kestrel concurrent reverse-order 2-reference imports: {overlapWatch.Elapsed.TotalMilliseconds:F0} ms, 2 created + 2 existing, no deadlock.");

            var beforeConflict = await CountsAsync(database);
            using (var conflict = await PostCsvAsync(http, operatorId, Csv(Row("KESTREL-NEW", name: "Changed recipient"), Row("KESTREL-CONFLICT-NEW"))))
            {
                Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
                var body = await conflict.Content.ReadAsStringAsync();
                Assert.Contains("NoProcesado", body, StringComparison.Ordinal);
            }
            Assert.Equal(beforeConflict, await CountsAsync(database));

            using (var malformed = await PostCsvAsync(http, operatorId, Header.Replace("codigoBulto", "wrong", StringComparison.Ordinal) + "\n" + Row("BAD-HEADER")))
                Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);

            var exactLimit = CreateBoundaryCsv(MaximumFileBytes);
            Assert.Equal(MaximumFileBytes, exactLimit.LongLength);
            using (var boundary = await PostCsvBytesAsync(http, operatorId, exactLimit))
            {
                Assert.Equal(HttpStatusCode.OK, boundary.StatusCode);
                Assert.Equal("Creado", FirstState(await boundary.Content.ReadAsStringAsync()));
            }
            using (var overLimit = await PostCsvBytesAsync(http, operatorId, CreateBoundaryCsv(MaximumFileBytes + 1)))
                Assert.Equal(HttpStatusCode.RequestEntityTooLarge, overLimit.StatusCode);

            using var commerceHttp = new HttpClient();
            var template = await commerceHttp.GetAsync($"{commerceUrl}/plantillas/alta-envios.csv");
            using (template)
            {
                Assert.Equal(HttpStatusCode.OK, template.StatusCode);
                var templateText = await template.Content.ReadAsStringAsync();
                Assert.Equal(Header, templateText.Split('\n', 2)[0].TrimEnd('\r'));
            }

            var browserCsvPath = Path.Combine(tempRoot, "commerce-import.csv");
            await File.WriteAllTextAsync(browserCsvPath, Csv(Row("COMMERCE-BROWSER")), new UTF8Encoding(false));
            var chromium = FindChromium();
            Assert.True(File.Exists(chromium), "A cached Chromium executable is required for the browser verification.");
            var browser = await ChromiumPage.StartAsync(chromium, Path.Combine(tempRoot, "chrome-profile"), commerceUrl + "/ingresar");
            browserProcess = browser;
            await browser.LoginAsync(commerceEmail, AuthenticationTestData.Password);
            var beforeBrowserIndividual = await CountsAsync(database);
            await browser.NavigateAsync(commerceUrl + "/envios/crear");
            await browser.WaitForAsync($"document.querySelector('#envio-operador option[value=\\\"{operatorId:D}\\\"]') !== null");
            await browser.CreateIndividualShipmentAsync(operatorId);
            var afterBrowserIndividual = await CountsAsync(database);
            Assert.Equal(beforeBrowserIndividual.Shipments + 1, afterBrowserIndividual.Shipments);
            Assert.Equal(beforeBrowserIndividual.Outbox + 2, afterBrowserIndividual.Outbox);

            await browser.NavigateAsync(commerceUrl + "/envios/importar");
            await browser.WaitForAsync($"document.querySelector('#import-operator option[value=\\\"{operatorId:D}\\\"]') !== null");
            await browser.SelectFileAndOperatorAsync(browserCsvPath, operatorId);
            var beforeBrowserCreate = await CountsAsync(database);
            await browser.ClickImportAndWaitAsync("Creado", doubleClick: true);
            var afterBrowserCreate = await CountsAsync(database);
            Assert.Equal(beforeBrowserCreate.Shipments + 1, afterBrowserCreate.Shipments);
            Assert.Equal(beforeBrowserCreate.Outbox + 2, afterBrowserCreate.Outbox);
            await browser.ClickImportAndWaitAsync("Ya existente");
            Assert.Equal(afterBrowserCreate, await CountsAsync(database));

            await using (var db = database.CreateDbContext())
            {
                var operatorRecord = await db.Operadores.SingleAsync(item => item.Id == operatorId);
                operatorRecord.CambiarEstado(EstadoOrganizacion.Suspendida);
                await db.SaveChangesAsync();
            }
            await browser.NavigateAsync(commerceUrl + "/envios/importar");
            await browser.WaitForAsync($"document.querySelector('#import-operator option[value=\\\"{operatorId:D}\\\"]')?.textContent.includes('inactivo') === true");
            await browser.SelectFileAndOperatorAsync(browserCsvPath, operatorId);
            await browser.ClickImportAndWaitAsync("Ya existente");

            var newInactivePath = Path.Combine(tempRoot, "inactive-new.csv");
            await File.WriteAllTextAsync(newInactivePath,
                Csv(Row("COMMERCE-INACTIVE-NEW"), Row("COMMERCE-INACTIVE-NEW")), new UTF8Encoding(false));
            var beforeInactiveRejection = await CountsAsync(database);
            await browser.SelectFileAndOperatorAsync(newInactivePath, operatorId);
            await browser.ClickImportAndWaitForRejectionAsync();
            await browser.WaitForAsync("document.querySelector('.import-result-list')?.innerText.includes('Duplicado de la fila 1; no se procesó porque el archivo se rechazó en forma atómica.') === true");
            Assert.Equal(beforeInactiveRejection, await CountsAsync(database));

            await browser.ScrollResultsIntoViewAsync();
            await browser.SetViewportAsync(1280, 900, mobile: false);
            await browser.CaptureScreenshotAsync(Path.Combine(tempRoot, "desktop.png"));
            await browser.SetViewportAsync(320, 900, mobile: true);
            var noHorizontalOverflow = await browser.EvaluateBooleanAsync("document.documentElement.scrollWidth <= window.innerWidth");
            Assert.True(noHorizontalOverflow, "Commerce import page horizontally overflows at 320 CSS pixels.");
            await browser.CaptureScreenshotAsync(Path.Combine(tempRoot, "mobile.png"));
            await browser.VerifyKeyboardFocusAsync();

            await using (var db = database.CreateDbContext())
            {
                var access = await db.AccesosUsuario.SingleAsync(item => item.Id == commerceAccessId);
                access.CambiarEstado(false);
                await db.SaveChangesAsync();
            }
            using (var expiredSession = await PostCsvAsync(http, operatorId, Csv(Row("EXPIRED-SESSION"))))
                Assert.Equal(HttpStatusCode.Unauthorized, expiredSession.StatusCode);
            await browser.NavigateAsync(commerceUrl + "/envios/importar");
            await browser.WaitForAsync("location.pathname === '/ingresar' && document.body.innerText.includes('sesi')");

            var finalCounts = await CountsAsync(database);
            Assert.Equal(finalCounts.Shipments * 2, finalCounts.Outbox);
            Console.WriteLine($"Kestrel+Commerce browser evidence passed. Isolated DB counts: {finalCounts.Shipments} shipments / {finalCounts.Outbox} outbox. Screenshots: {tempRoot}");
        }
        finally
        {
            if (browserProcess is not null) await browserProcess.DisposeAsync();
            if (commerceProcess is not null) await commerceProcess.DisposeAsync();
            if (apiProcess is not null) await apiProcess.DisposeAsync();
            if (Directory.Exists(tempRoot) && Path.GetDirectoryName(Path.GetFullPath(tempRoot)) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task BackofficeBrowserReprogramsFailedDeliveryAndShowsRuleBoundEligibility()
    {
        var database = await postgres.CreateDatabaseAsync();
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nexoruta-dec04-browser-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        KestrelProcess? apiProcess = null;
        KestrelProcess? backofficeProcess = null;
        ChromiumPage? browser = null;
        try
        {
            const string commerceEmail = "dec04-browser-commerce@example.test";
            const string operatorEmail = "dec04-browser-operator@example.test";
            var seedSettings = AuthenticationTestData.Settings();
            seedSettings["AccesoInicial:UsuarioEmail"] = commerceEmail;
            seedSettings["AccesoInicial:OperadorUsuarioEmail"] = operatorEmail;
            seedSettings["DataProtection:KeysDirectory"] = Path.Combine(tempRoot, "api-keys");
            using (var bootstrap = new IntegrationApiFactory(database, settings: seedSettings))
            using (bootstrap.CreateHttpClient()) { }

            Guid operatorId;
            Guid operatorAccessId;
            await using (var db = database.CreateDbContext())
            {
                operatorId = await db.Operadores.Select(x => x.Id).SingleAsync();
                operatorAccessId = await (from access in db.AccesosUsuario
                    join user in db.Usuarios on access.UsuarioId equals user.Id
                    where user.Email == operatorEmail && access.OperadorId == operatorId
                    select access.Id).SingleAsync();
            }

            var runtimeConnection = await CreateRestrictedApiConnectionAsync(database);
            var apiPort = FindFreePort();
            var backofficePort = FindFreePort();
            var apiUrl = $"http://127.0.0.1:{apiPort}";
            var backofficeUrl = $"http://127.0.0.1:{backofficePort}";
            var root = FindWorkspaceRoot();
            var apiDll = Path.Combine(root, "backend", "src", "NexoRuta.Api", "bin", "Debug", "net10.0", "NexoRuta.Api.dll");
            var backofficeDll = Path.Combine(root, "frontend", "src", "NexoRuta.Backoffice", "bin", "Debug", "net10.0", "NexoRuta.Backoffice.dll");
            Assert.True(File.Exists(apiDll), "Build the API before the isolated browser check.");
            Assert.True(File.Exists(backofficeDll), "Build Backoffice before the browser check.");

            apiProcess = StartDotnet(apiDll, Path.GetDirectoryName(apiDll)!, apiUrl, new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["ConnectionStrings__Postgres"] = runtimeConnection,
                ["DatabaseInitialization__ApplyMigrations"] = "false",
                ["DatabaseInitialization__SeedData"] = "false",
                ["OpenTelemetry__Enabled"] = "false",
                ["Jwt__Issuer"] = AuthenticationTestData.Issuer,
                ["Jwt__Audience"] = AuthenticationTestData.Audience,
                ["Jwt__SigningKeyBase64"] = AuthenticationTestData.SigningKey,
                ["DataProtection__KeysDirectory"] = Path.Combine(tempRoot, "api-keys"),
                ["Tracking__PublicBaseUrl"] = "https://tracking.example.test",
                ["PasswordRecovery__ResetPageUrl"] = "https://commerce.example.test/restablecer-contrasena",
                ["PasswordRecovery__TokenLifespanSeconds"] = "900",
                ["Email__Smtp__Host"] = "mailpit.integration.test",
                ["Email__Smtp__Port"] = "1025",
                ["Email__Smtp__UseSsl"] = "false",
                ["Email__Smtp__FromAddress"] = "noreply@example.test",
                ["Email__Smtp__FromName"] = "NexoRuta integration"
            });
            await WaitForHttpAsync(apiProcess.Process, $"{apiUrl}/health/live");

            backofficeProcess = StartDotnet(backofficeDll, Path.GetDirectoryName(backofficeDll)!, backofficeUrl,
                new Dictionary<string, string?>
                {
                    ["ASPNETCORE_ENVIRONMENT"] = "Development",
                    ["Api__BaseAddress"] = apiUrl + "/",
                    ["DataProtection__KeysDirectory"] = Path.Combine(tempRoot, "backoffice-keys")
                });
            await WaitForHttpAsync(backofficeProcess.Process, $"{backofficeUrl}/health/ready");

            using var api = new HttpClient { BaseAddress = new Uri(apiUrl), Timeout = TimeSpan.FromSeconds(45) };
            var commerceToken = await LoginAsync(api, commerceEmail, AuthenticationTestData.Password);
            api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", commerceToken);
            using var shipmentResponse = await api.PostAsJsonAsync("/api/envios", new
            {
                OperadorId = operatorId,
                DestinatarioNombre = "DEC04 browser recipient",
                DestinatarioEmail = "dec04-recipient@example.test",
                Direccion = "18 de Julio 123",
                CodigoBulto = "DEC04-BROWSER",
                PesoGramos = 500m,
                LargoCentimetros = 20m,
                AnchoCentimetros = 15m,
                AltoCentimetros = 10m
            });
            Assert.Equal(HttpStatusCode.Created, shipmentResponse.StatusCode);
            using var shipmentJson = JsonDocument.Parse(await shipmentResponse.Content.ReadAsStringAsync());
            var shipmentId = shipmentJson.RootElement.GetProperty("id").GetGuid();
            using var destination = await api.PutAsJsonAsync($"/api/envios/{shipmentId}/destino",
                new { Version = 0, Latitud = -34.9, Longitud = -56.2 });
            Assert.Equal(HttpStatusCode.NoContent, destination.StatusCode);

            var operatorToken = await LoginAsync(api, operatorEmail, AuthenticationTestData.Password);
            api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", operatorToken);
            using var routeRules = await api.PutAsJsonAsync("/api/reparto/configuracion", new
            {
                Version = 0, ZonaHoraria = "America/Montevideo", MaximoParadas = 30,
                RequiereFotoIntento = false, BloquearDiscrepanciasCarga = true
            });
            Assert.Equal(HttpStatusCode.OK, routeRules.StatusCode);
            using var courier = await api.PutAsJsonAsync($"/api/repartidores/{operatorAccessId}",
                new { Nombre = "DEC04 browser courier", Activo = true });
            Assert.Equal(HttpStatusCode.OK, courier.StatusCode);
            using var vehicleResponse = await api.PutAsJsonAsync("/api/reparto/vehiculos", new
            {
                Matricula = "DEC404", PesoMaximoGramos = 10000000m, VolumenMaximoCm3 = 100000000000m, Activo = true
            });
            Assert.Equal(HttpStatusCode.OK, vehicleResponse.StatusCode);
            using var vehicleJson = JsonDocument.Parse(await vehicleResponse.Content.ReadAsStringAsync());
            var vehicleId = vehicleJson.RootElement.GetProperty("id").GetGuid();
            using var received = await api.PostAsJsonAsync($"/api/deposito/envios/{shipmentId}/recepcion", new { Version = 0 });
            Assert.True(received.StatusCode == HttpStatusCode.NoContent,
                $"Deposit receipt failed: {(int)received.StatusCode} {await received.Content.ReadAsStringAsync()}");

            var fecha = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
                TimeZoneInfo.FindSystemTimeZoneById("America/Montevideo")).DateTime);
            var llegada = new DateTimeOffset(fecha.ToDateTime(new TimeOnly(15, 0)), TimeSpan.Zero);
            using var planned = await api.PostAsJsonAsync("/api/rutas", new
            {
                RepartidorAccesoId = operatorAccessId, VehiculoId = vehicleId, Fecha = fecha,
                Paradas = new[] { new { EnvioId = shipmentId, LlegadaEstimadaUtc = llegada } }
            });
            Assert.Equal(HttpStatusCode.OK, planned.StatusCode);
            using var plannedJson = JsonDocument.Parse(await planned.Content.ReadAsStringAsync());
            var routeId = plannedJson.RootElement.GetProperty("id").GetGuid();
            var routeVersion = plannedJson.RootElement.GetProperty("version").GetInt64();

            using var dispatched = await api.PostAsJsonAsync($"/api/rutas/{routeId}/despacho", new { Version = routeVersion });
            Assert.Equal(HttpStatusCode.OK, dispatched.StatusCode);
            using var routeList = await api.GetAsync($"/api/mobile/rutas?fecha={fecha:yyyy-MM-dd}");
            Assert.Equal(HttpStatusCode.OK, routeList.StatusCode);
            using var routeJson = JsonDocument.Parse(await routeList.Content.ReadAsStringAsync());
            var route = routeJson.RootElement.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == routeId);
            var stop = route.GetProperty("paradas")[0];
            var jornadaId = route.GetProperty("jornadaId").GetGuid();
            var envioVersion = stop.GetProperty("envioVersion").GetInt64();
            var bultoId = stop.GetProperty("bultos")[0].GetProperty("id").GetGuid();

            async Task<string> ApplyMobileAsync(long sequence, string type, long? version = null, object? payload = null)
            {
                using var response = await api.PostAsJsonAsync($"/api/mobile/jornadas/{jornadaId}/operaciones", new
                {
                    Id = Guid.NewGuid(), Secuencia = sequence, Tipo = type,
                    EnvioId = type is "IntentoFallido" or "CargarBulto" ? shipmentId : (Guid?)null,
                    VersionEnvio = type is "IntentoFallido" or "CargarBulto" ? version : null,
                    OcurridaEn = DateTimeOffset.UtcNow,
                    Datos = payload ?? new { }
                });
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                return result.RootElement.GetProperty("resultado").GetString()!;
            }
            Assert.Equal("Aplicada", await ApplyMobileAsync(1, "InicioJornada"));
            Assert.Equal("Aplicada", await ApplyMobileAsync(2, "CargarBulto", envioVersion, new { BultoId = bultoId }));
            Assert.Equal("Aplicada", await ApplyMobileAsync(3, "ConfirmarCarga"));
            Assert.Equal("Aplicada", await ApplyMobileAsync(4, "IntentoFallido", envioVersion + 1, new { Motivo = "AUSENTE" }));

            var chromium = FindChromium();
            Assert.True(File.Exists(chromium), "A cached Chromium executable is required for this isolated browser check.");
            browser = await ChromiumPage.StartAsync(chromium, Path.Combine(tempRoot, "chrome-profile"), backofficeUrl + "/ingresar");
            await browser.LoginAsync(operatorEmail, AuthenticationTestData.Password, "/");
            await browser.NavigateAsync($"{backofficeUrl}/rutas?fecha={fecha:yyyy-MM-dd}");
            await browser.WaitForAsync("document.body.innerText.includes('Requiere reprogramación a una ruta nueva')");
            Assert.False(await browser.EvaluateBooleanAsync("Array.from(document.querySelectorAll('button')).some(button=>button.innerText.includes('Reasignar a esa ruta'))"),
                "A shipment awaiting reprogramming must not expose active-route reassignment.");
            var attemptSummary = await browser.EvaluateStringAsync("Array.from(document.querySelectorAll('tr')).find(row=>row.innerText.includes('Requiere reprogramación'))?.innerText || ''");
            Assert.Contains("1 de 3", attemptSummary);
            Assert.Contains("Próxima tentativa elegible desde", attemptSummary);
            await browser.SetViewportAsync(1280, 900, mobile: false);
            await browser.CaptureScreenshotAsync(Path.Combine(tempRoot, "desktop.png"));
            await browser.SetViewportAsync(320, 900, mobile: true);
            Assert.True(await browser.EvaluateBooleanAsync("document.documentElement.scrollWidth <= window.innerWidth"),
                "Backoffice routes horizontally overflow at 320 CSS pixels.");
            await browser.CaptureScreenshotAsync(Path.Combine(tempRoot, "mobile.png"));
            await browser.VerifyKeyboardFocusAsync();
            await browser.ClickButtonContainingAsync("Reprogramar para una ruta nueva");
            await browser.WaitForAsync("document.querySelector('.alert-success')?.innerText.includes('Envío reprogramado') === true");
            Assert.False(await browser.EvaluateBooleanAsync("Array.from(document.querySelectorAll('button')).some(button=>button.innerText.includes('Reprogramar para una ruta nueva'))"));

            await using var verify = database.CreateDbContext();
            var shipment = await verify.Envios.SingleAsync(x => x.Id == shipmentId);
            Assert.Equal("Reprogramado", shipment.Estado.ToString());
            Assert.Equal(1, await verify.EnvioEventos.CountAsync(x => x.EnvioId == shipmentId
                && x.EstadoAnterior == "NoEntregado" && x.EstadoNuevo == "Reprogramado"));
            output.WriteLine($"Backoffice Chromium desktop/mobile flow verified against local Kestrel and isolated restricted-API PostgreSQL. Screenshots were temporary under {tempRoot}.");
        }
        finally
        {
            if (browser is not null) await browser.DisposeAsync();
            if (backofficeProcess is not null) await backofficeProcess.DisposeAsync();
            if (apiProcess is not null) await apiProcess.DisposeAsync();
            if (Directory.Exists(tempRoot) && Path.GetDirectoryName(Path.GetFullPath(tempRoot)) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar))
            {
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        Directory.Delete(tempRoot, recursive: true);
                        break;
                    }
                    catch (IOException) when (attempt < 9)
                    {
                        await Task.Delay(200);
                    }
                }
            }
        }
    }

    [Fact]
    public async Task BackofficeBrowserReassignsEligibleShipmentAndPersistsOneEffect()
    {
        var database = await postgres.CreateDatabaseAsync();
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nexoruta-dec07-reassign-browser-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var screenshotDirectory = Environment.GetEnvironmentVariable("NEXORUTA_RUTAS_SCREENSHOT_DIR");
        if (!string.IsNullOrWhiteSpace(screenshotDirectory)) Directory.CreateDirectory(screenshotDirectory);
        string ScreenshotPath(string name) => Path.Combine(
            string.IsNullOrWhiteSpace(screenshotDirectory) ? tempRoot : screenshotDirectory, name);
        KestrelProcess? apiProcess = null;
        KestrelProcess? backofficeProcess = null;
        ChromiumPage? browser = null;
        try
        {
            const string commerceEmail = "dec07-browser-commerce@example.test";
            const string operatorEmail = "dec07-browser-operator@example.test";
            var seedSettings = AuthenticationTestData.Settings();
            seedSettings["AccesoInicial:UsuarioEmail"] = commerceEmail;
            seedSettings["AccesoInicial:OperadorUsuarioEmail"] = operatorEmail;
            seedSettings["DataProtection:KeysDirectory"] = Path.Combine(tempRoot, "api-keys");
            using (var bootstrap = new IntegrationApiFactory(database, settings: seedSettings))
            using (bootstrap.CreateHttpClient()) { }

            Guid operatorId;
            Guid operatorAccessId;
            Guid destinationCourierAccessId;
            await using (var db = database.CreateDbContext())
            {
                operatorId = await db.Operadores.Select(x => x.Id).SingleAsync();
                var operatorAccess = await (from access in db.AccesosUsuario
                    join account in db.Usuarios on access.UsuarioId equals account.Id
                    where account.Email == operatorEmail && access.OperadorId == operatorId
                    select access).SingleAsync();
                operatorAccessId = operatorAccess.Id;
                var operatorRole = await db.Roles.SingleAsync(x => x.Id == operatorAccess.RolId);
                var courierUser = new Usuario("dec07-destination-courier@example.test", AuthenticationTestData.Hash("prueba"));
                db.Usuarios.Add(courierUser);
                await db.SaveChangesAsync();
                db.AccesosUsuario.Add(new AccesoUsuario(courierUser.Id, operatorId, null, operatorRole));
                await db.SaveChangesAsync();
                destinationCourierAccessId = await db.AccesosUsuario
                    .Where(x => x.UsuarioId == courierUser.Id && x.OperadorId == operatorId)
                    .Select(x => x.Id).SingleAsync();
            }

            var runtimeConnection = await CreateRestrictedApiConnectionAsync(database);
            var apiPort = FindFreePort();
            var backofficePort = FindFreePort();
            var apiUrl = $"http://127.0.0.1:{apiPort}";
            var backofficeUrl = $"http://127.0.0.1:{backofficePort}";
            var root = FindWorkspaceRoot();
            var apiDll = Path.Combine(root, "backend", "src", "NexoRuta.Api", "bin", "Debug", "net10.0", "NexoRuta.Api.dll");
            var backofficeDll = Path.Combine(root, "frontend", "src", "NexoRuta.Backoffice", "bin", "Debug", "net10.0", "NexoRuta.Backoffice.dll");
            Assert.True(File.Exists(apiDll), "Build the API before the isolated browser check.");
            Assert.True(File.Exists(backofficeDll), "Build Backoffice before the browser check.");

            apiProcess = StartDotnet(apiDll, Path.GetDirectoryName(apiDll)!, apiUrl, new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["ConnectionStrings__Postgres"] = runtimeConnection,
                ["DatabaseInitialization__ApplyMigrations"] = "false",
                ["DatabaseInitialization__SeedData"] = "false",
                ["OpenTelemetry__Enabled"] = "false",
                ["Jwt__Issuer"] = AuthenticationTestData.Issuer,
                ["Jwt__Audience"] = AuthenticationTestData.Audience,
                ["Jwt__SigningKeyBase64"] = AuthenticationTestData.SigningKey,
                ["DataProtection__KeysDirectory"] = Path.Combine(tempRoot, "api-keys"),
                ["Tracking__PublicBaseUrl"] = "https://tracking.example.test",
                ["PasswordRecovery__ResetPageUrl"] = "https://commerce.example.test/restablecer-contrasena",
                ["PasswordRecovery__TokenLifespanSeconds"] = "900",
                ["Email__Smtp__Host"] = "mailpit.integration.test",
                ["Email__Smtp__Port"] = "1025",
                ["Email__Smtp__UseSsl"] = "false",
                ["Email__Smtp__FromAddress"] = "noreply@example.test",
                ["Email__Smtp__FromName"] = "NexoRuta integration"
            });
            await WaitForHttpAsync(apiProcess.Process, $"{apiUrl}/health/live");
            backofficeProcess = StartDotnet(backofficeDll, Path.GetDirectoryName(backofficeDll)!, backofficeUrl,
                new Dictionary<string, string?>
                {
                    ["ASPNETCORE_ENVIRONMENT"] = "Development",
                    ["Api__BaseAddress"] = apiUrl + "/",
                    ["DataProtection__KeysDirectory"] = Path.Combine(tempRoot, "backoffice-keys")
                });
            await WaitForHttpAsync(backofficeProcess.Process, $"{backofficeUrl}/health/ready");

            using var api = new HttpClient { BaseAddress = new Uri(apiUrl), Timeout = TimeSpan.FromSeconds(45) };
            var commerceToken = await LoginAsync(api, commerceEmail, AuthenticationTestData.Password);
            api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", commerceToken);
            using var created = await api.PostAsJsonAsync("/api/envios", new
            {
                OperadorId = operatorId,
                DestinatarioNombre = "DEC07 browser reassignment recipient",
                DestinatarioEmail = "dec07-recipient@example.test",
                Direccion = "18 de Julio 123",
                CodigoBulto = "DEC07-BROWSER-REASSIGN",
                PesoGramos = 500m,
                LargoCentimetros = 20m,
                AnchoCentimetros = 15m,
                AltoCentimetros = 10m
            });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var shipmentId = createdJson.RootElement.GetProperty("id").GetGuid();
            using var receiptCreated = await api.PostAsJsonAsync("/api/envios", new
            {
                OperadorId = operatorId,
                DestinatarioNombre = "DEC07 browser first receipt",
                DestinatarioEmail = "dec07-receipt@example.test",
                Direccion = "18 de Julio 456",
                CodigoBulto = "DEC07-BROWSER-RECEIPT",
                PesoGramos = 750m,
                LargoCentimetros = 21m,
                AnchoCentimetros = 16m,
                AltoCentimetros = 11m
            });
            Assert.Equal(HttpStatusCode.Created, receiptCreated.StatusCode);
            using var receiptCreatedJson = JsonDocument.Parse(await receiptCreated.Content.ReadAsStringAsync());
            var receiptShipmentId = receiptCreatedJson.RootElement.GetProperty("id").GetGuid();
            using var planningCandidate = await api.PostAsJsonAsync("/api/envios", new
            {
                OperadorId = operatorId,
                DestinatarioNombre = "ZZZ DEC07 optional planning candidate",
                DestinatarioEmail = "dec07-candidate@example.test",
                Direccion = "18 de Julio 789",
                CodigoBulto = "DEC07-BROWSER-CANDIDATE",
                PesoGramos = 250m,
                LargoCentimetros = 12m,
                AnchoCentimetros = 10m,
                AltoCentimetros = 8m
            });
            Assert.Equal(HttpStatusCode.Created, planningCandidate.StatusCode);
            using var planningCandidateJson = JsonDocument.Parse(await planningCandidate.Content.ReadAsStringAsync());
            var planningCandidateId = planningCandidateJson.RootElement.GetProperty("id").GetGuid();
            using var destination = await api.PutAsJsonAsync($"/api/envios/{shipmentId}/destino",
                new { Version = 0, Latitud = -34.9, Longitud = -56.2 });
            Assert.Equal(HttpStatusCode.NoContent, destination.StatusCode);
            using var candidateDestination = await api.PutAsJsonAsync($"/api/envios/{planningCandidateId}/destino",
                new { Version = 0, Latitud = -34.91, Longitud = -56.21 });
            Assert.Equal(HttpStatusCode.NoContent, candidateDestination.StatusCode);

            var operatorToken = await LoginAsync(api, operatorEmail, AuthenticationTestData.Password);
            api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", operatorToken);
            using var rules = await api.PutAsJsonAsync("/api/reparto/configuracion", new
            {
                Version = 0, ZonaHoraria = "America/Montevideo", MaximoParadas = 30,
                RequiereFotoIntento = false, BloquearDiscrepanciasCarga = true
            });
            Assert.Equal(HttpStatusCode.OK, rules.StatusCode);
            using var sourceCourier = await api.PutAsJsonAsync($"/api/repartidores/{operatorAccessId}",
                new { Nombre = "DEC07 source courier", Activo = true });
            Assert.Equal(HttpStatusCode.OK, sourceCourier.StatusCode);
            using var targetCourier = await api.PutAsJsonAsync($"/api/repartidores/{destinationCourierAccessId}",
                new { Nombre = "DEC07 destination courier", Activo = true });
            Assert.Equal(HttpStatusCode.OK, targetCourier.StatusCode);

            using var sourceVehicleResponse = await api.PutAsJsonAsync("/api/reparto/vehiculos", new
            {
                Matricula = "DEC07SRC", PesoMaximoGramos = 50000m, VolumenMaximoCm3 = 300000m, Activo = true
            });
            Assert.Equal(HttpStatusCode.OK, sourceVehicleResponse.StatusCode);
            using var sourceVehicleJson = JsonDocument.Parse(await sourceVehicleResponse.Content.ReadAsStringAsync());
            using var targetVehicleResponse = await api.PutAsJsonAsync("/api/reparto/vehiculos", new
            {
                Matricula = "DEC07DST", PesoMaximoGramos = 50000m, VolumenMaximoCm3 = 300000m, Activo = true
            });
            Assert.Equal(HttpStatusCode.OK, targetVehicleResponse.StatusCode);
            using var targetVehicleJson = JsonDocument.Parse(await targetVehicleResponse.Content.ReadAsStringAsync());

            using var received = await api.PostAsJsonAsync($"/api/deposito/envios/{shipmentId}/recepcion", new { Version = 0 });
            Assert.Equal(HttpStatusCode.NoContent, received.StatusCode);
            using var receivedCandidate = await api.PostAsJsonAsync($"/api/deposito/envios/{planningCandidateId}/recepcion", new { Version = 0 });
            Assert.Equal(HttpStatusCode.NoContent, receivedCandidate.StatusCode);
            var fecha = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
                TimeZoneInfo.FindSystemTimeZoneById("America/Montevideo")).DateTime);
            var fechaSeleccionada = fecha.AddDays(1);
            var fechaDeReservaOriginal = fecha;
            var sourceVehicleId = sourceVehicleJson.RootElement.GetProperty("id").GetGuid();
            using var reservaVaciaOriginal = await api.PostAsJsonAsync("/api/rutas", new
            {
                RepartidorAccesoId = operatorAccessId,
                VehiculoId = sourceVehicleId,
                Fecha = fechaDeReservaOriginal,
                Paradas = Array.Empty<object>()
            });
            Assert.Equal(HttpStatusCode.OK, reservaVaciaOriginal.StatusCode);
            using var reservaVaciaJson = JsonDocument.Parse(await reservaVaciaOriginal.Content.ReadAsStringAsync());
            var reservaVaciaId = reservaVaciaJson.RootElement.GetProperty("id").GetGuid();
            await using (var reservedDay = database.CreateDbContext())
                Assert.True((await reservedDay.HojasRuta.SingleAsync(x => x.Id == reservaVaciaId)).ReservaRecursosActiva);

            var chromium = FindChromium();
            Assert.True(File.Exists(chromium), "A cached Chromium executable is required for this isolated browser check.");
            browser = await ChromiumPage.StartAsync(chromium, Path.Combine(tempRoot, "chrome-profile"), backofficeUrl + "/ingresar");
            await browser.LoginAsync(operatorEmail, AuthenticationTestData.Password, "/");
            await browser.SetViewportAsync(1513, 1039, mobile: false);
            await browser.NavigateAsync(backofficeUrl + "/");
            await browser.WaitForAsync("document.querySelector('#main-content') !== null");
            Assert.True(await browser.EvaluateBooleanAsync("(()=>{const shell=document.querySelector('#backoffice-app-shell');return Boolean(shell&&shell.querySelectorAll(':scope > .nr-topbar').length===1&&shell.querySelectorAll(':scope > .nr-portal-layout').length===1&&shell.querySelectorAll('#backofficeNavigation.nr-sidebar').length===1&&shell.querySelectorAll('#main-content').length===1&&document.querySelectorAll('.nr-routes-app,.nr-routes-sidebar,.nr-routes-topbar').length===0&&document.querySelectorAll('#backofficeNavigation').length===1&&document.querySelectorAll('.nr-topbar').length===1&&document.querySelector('#backofficeNavigation a.active')?.getAttribute('href')==='/');})()"),
                "Envíos should use exactly one original Backoffice shell and its shared active navigation.");
            Assert.Contains("Cerrar sesión", await browser.EvaluateStringAsync("document.querySelector('.nr-topbar-actions')?.innerText || ''"), StringComparison.Ordinal);
            var originalShellSignature = await browser.GetSharedShellSignatureAsync();
            await browser.CaptureScreenshotAsync(ScreenshotPath("shell-envios-1513x1039.png"));

            await browser.NavigateAsync(backofficeUrl + "/configuracion-reparto");
            await browser.WaitForAsync("location.pathname.toLowerCase()==='/configuracion-reparto'&&document.querySelector('#main-content')!==null");
            Assert.True(await browser.EvaluateBooleanAsync("document.querySelector('#backofficeNavigation a.active')?.getAttribute('href')==='/configuracion-reparto'"),
                "The shared navigation should mark only the configuration destination as active.");
            Assert.True(await browser.HasSingleSharedBackofficeShellAsync(), "Configuration should not introduce a second or alternate Backoffice shell.");
            Assert.Equal(originalShellSignature, await browser.GetSharedShellSignatureAsync());
            await browser.CaptureScreenshotAsync(ScreenshotPath("shell-configuracion-1513x1039.png"));

            await browser.NavigateAsync($"{backofficeUrl}/rutas/planificar?fecha={fecha:yyyy-MM-dd}");
            await browser.WaitForAsync("document.querySelector('[data-plan-row] [data-plan-check]') !== null");
            Assert.True(await browser.EvaluateBooleanAsync("document.querySelector('#backofficeNavigation a.active')?.getAttribute('href')==='/rutas'"),
                "Planning should reuse the shared Rutas navigation entry rather than create another navigation.");
            Assert.True(await browser.HasSingleSharedBackofficeShellAsync(), "Planning should not introduce a second or alternate Backoffice shell.");
            Assert.Equal(originalShellSignature, await browser.GetSharedShellSignatureAsync());
            var populatePlannerScript = $"(()=>{{const courier=document.querySelector('#RepartidorAccesoId');const vehicle=document.querySelector('#VehiculoId');const row=[...document.querySelectorAll('[data-plan-row]')].find(r=>r.dataset.search?.includes('DEC07-BROWSER-REASSIGN'));const check=row?.querySelector('[data-plan-check]');const order=row?.querySelector('[data-stop-order]');const time=row?.querySelector('[data-stop-time]');if(!courier||!vehicle||!check||!order||!time)return false;courier.value={JsonSerializer.Serialize(operatorAccessId.ToString("D"))};courier.dispatchEvent(new Event('change',{{bubbles:true}}));vehicle.value={JsonSerializer.Serialize(sourceVehicleJson.RootElement.GetProperty("id").GetGuid().ToString("D"))};vehicle.dispatchEvent(new Event('change',{{bubbles:true}}));check.checked=true;check.dispatchEvent(new Event('change',{{bubbles:true}}));order.value='123';order.dispatchEvent(new Event('input',{{bubbles:true}}));time.value='17:30';time.dispatchEvent(new Event('input',{{bubbles:true}}));return true;}})()";
            Assert.True(await browser.EvaluateBooleanAsync(populatePlannerScript), "An eligible real shipment, operator and vehicle should populate the planner.");
            await browser.WaitForAsync("document.querySelector('[data-summary-count]')?.innerText === '1' && document.querySelector('[data-summary-time]')?.innerText === '17:30' && document.querySelectorAll('[data-plan-row]').length === 2 && document.querySelectorAll('[data-plan-row].is-selected').length === 1");
            Assert.True(await browser.EvaluateBooleanAsync("!document.querySelector('.nr-summary-first-stop') && !document.querySelector('.nr-summary-conditions h3')"),
                "The planning summary should not repeat recipient/address or add a redundant nested conditions heading.");
            var vehicleCapacity = await browser.EvaluateStringAsync("document.querySelector('#VehiculoId option:checked')?.getAttribute('data-weight-capacity') || 'missing'");
            Assert.True(decimal.TryParse(vehicleCapacity, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedCapacity) && parsedCapacity > 0,
                $"The selected vehicle must expose its configured capacity to the preview. Observed '{vehicleCapacity}'.");
            var capacitySummary = await browser.EvaluateStringAsync("JSON.stringify({weight:document.querySelector('#VehiculoId option:checked')?.dataset.weightCapacity,label:document.querySelector('[data-capacity-label]')?.innerText})");
            Assert.True(await browser.EvaluateBooleanAsync("document.querySelector('[data-capacity-label]')?.innerText.includes('kg de capacidad') && document.querySelector('[data-capacity-label]')?.innerText.includes('m³') && !document.querySelector('[data-capacity-label]')?.innerText.includes('cm³')"),
                $"Vehicle capacity should use the readable units shown in the approved reference: {capacitySummary}");
            Assert.Equal("Fecha de salida", await browser.EvaluateStringAsync("document.querySelector('label[for=Fecha]')?.innerText.trim() || ''"));
            var culturaResumen = CultureInfo.GetCultureInfo("es-UY");
            var fechaResumenEsperada = fecha.ToDateTime(TimeOnly.MinValue).ToString("dddd d 'de' MMMM", culturaResumen);
            fechaResumenEsperada = char.ToUpper(fechaResumenEsperada[0], culturaResumen) + fechaResumenEsperada[1..];
            Assert.Equal(fechaResumenEsperada, await browser.EvaluateStringAsync("document.querySelector('[data-summary-date]')?.innerText.trim() || ''"));
            Assert.True(await browser.EvaluateBooleanAsync("!document.querySelector('[data-capacity-label]')?.innerText.includes('Seleccioná un vehículo')"),
                $"Selected vehicle capacity was not reflected next to the selector: {capacitySummary}");
            Assert.True(await browser.EvaluateBooleanAsync("(()=>{const page=document.querySelector('.nr-routes-page');const family=getComputedStyle(page).fontFamily;return family.includes('sans-serif')&&[...document.querySelectorAll('.nr-planning-summary,.nr-planning-summary dt,.nr-planning-summary button,.nr-planning-row.is-selected [data-stop-order],.nr-planning-row.is-selected [data-stop-time]')].every(x=>getComputedStyle(x).fontFamily===family);})()"),
                "Planning headings, summary, buttons and native controls should share the project sans-serif family.");
            var renderedFontEvidence = string.Join(Environment.NewLine, new[]
            {
                $"Planning heading: {await browser.GetPlatformFontsAsync(".nr-planning-section h2")}",
                $"Selected shipment: {await browser.GetPlatformFontsAsync(".nr-planning-row.is-selected .nr-shipment-identity")}",
                $"Summary: {await browser.GetPlatformFontsAsync(".nr-planning-summary")}",
                $"Summary conditions: {await browser.GetPlatformFontsAsync(".nr-summary-conditions")}",
                $"Computed planning stacks and metrics: {await browser.EvaluateStringAsync("JSON.stringify(['.nr-planning-page','.nr-planning-section h2','.nr-planning-list-header','.nr-planning-summary','.nr-summary-kpis dt','.nr-summary-assignment strong','.nr-summary-conditions p','.nr-planning-submit button'].map(s=>{const e=document.querySelector(s);if(!e)return {selector:s,missing:true};const c=getComputedStyle(e);return {selector:s,family:c.fontFamily,size:c.fontSize,weight:c.fontWeight,lineHeight:c.lineHeight};}))") }"
            });
            var stopControlMetrics = await browser.EvaluateStringAsync("JSON.stringify((()=>{const order=document.querySelector('[data-plan-row].is-selected [data-stop-order]');const time=document.querySelector('[data-plan-row].is-selected [data-stop-time]');return {order:order?.value,orderWidth:order?.getBoundingClientRect().width,orderClient:order?.clientWidth,orderScroll:order?.scrollWidth,time:time?.value,timeWidth:time?.getBoundingClientRect().width,timeClient:time?.clientWidth,timeScroll:time?.scrollWidth};})())");
            Assert.True(await browser.EvaluateBooleanAsync("(()=>{const order=document.querySelector('[data-plan-row].is-selected [data-stop-order]');const time=document.querySelector('[data-plan-row].is-selected [data-stop-time]');return order?.value==='123'&&time?.value==='17:30'&&order.getBoundingClientRect().width>=80&&time.getBoundingClientRect().width>=120&&order.scrollWidth<=order.clientWidth&&time.scrollWidth<=time.clientWidth;})()"),
                $"Multi-digit stop order and the complete native time value must remain visible at the reference desktop viewport. {stopControlMetrics}");
            Assert.True(await browser.EvaluateBooleanAsync("document.querySelector('#eligibility-filter')?.value === 'eligible' && document.querySelector('[data-plan-row]')?.dataset.eligible === 'true'"));
            await browser.SetViewportAsync(1514, 1039, mobile: false);
            await browser.CaptureScreenshotAsync(ScreenshotPath("planificacion-1514x1039.png"));
            await browser.SetViewportAsync(1513, 1039, mobile: false);
            Assert.Equal(originalShellSignature, await browser.GetSharedShellSignatureAsync());
            await browser.CaptureScreenshotAsync(ScreenshotPath("shell-planificacion-1513x1039.png"));
            await browser.SetViewportAsync(320, 900, mobile: true);
            Assert.True(await browser.EvaluateBooleanAsync("document.documentElement.scrollWidth <= window.innerWidth"),
                "Route planning horizontally overflows at 320 CSS pixels.");
            await browser.CaptureScreenshotAsync(ScreenshotPath("planificacion-320x900.png"));
            await browser.CaptureScreenshotAsync(ScreenshotPath("planificacion-320x900-full.png"), fullPage: true);
            Assert.True(await browser.EvaluateBooleanAsync("(()=>{const list=document.querySelector('#planning-shipment-list');list?.scrollIntoView({block:'center'});const order=document.querySelector('[data-plan-row].is-selected [data-stop-order]');const time=document.querySelector('[data-plan-row].is-selected [data-stop-time]');return Boolean(order&&time&&order.value==='123'&&time.value==='17:30'&&order.getBoundingClientRect().width>=80&&time.getBoundingClientRect().width>=120);})()"),
                "At 320 CSS pixels, the planner's order and time inputs must remain reachable and legible.");
            Assert.True(await browser.EvaluateBooleanAsync("(()=>{const page=document.querySelector('.nr-routes-page');const family=getComputedStyle(page).fontFamily;return [...document.querySelectorAll('.nr-planning-summary,.nr-planning-summary dt,.nr-planning-summary button,.nr-planning-row.is-selected [data-stop-order],.nr-planning-row.is-selected [data-stop-time]')].every(x=>getComputedStyle(x).fontFamily===family);})()"));
            await browser.VerifyKeyboardFocusAsync();
            await browser.ClickLinkContainingKeyboardAsync("Cancelar");
            await browser.WaitForAsync("location.pathname.toLowerCase() === '/rutas'");
            await using (var afterPlannerCancel = database.CreateDbContext())
            {
                var retainedReservation = await afterPlannerCancel.HojasRuta.SingleAsync(x => x.Id == reservaVaciaId);
                Assert.Equal((fechaDeReservaOriginal, EstadoHojaRuta.Planificada, true),
                    (retainedReservation.Fecha, retainedReservation.Estado, retainedReservation.ReservaRecursosActiva));
            }

            await browser.NavigateAsync($"{backofficeUrl}/rutas/planificar?fecha={fecha:yyyy-MM-dd}");
            await browser.WaitForAsync("document.querySelector('[data-plan-row] [data-plan-check]') !== null");
            Assert.True(await browser.EvaluateBooleanAsync(populatePlannerScript));
            await browser.WaitForAsync("document.querySelector('[data-summary-count]')?.innerText === '1'");
            Assert.True(await browser.EvaluateBooleanAsync("(()=>{const order=document.querySelector('[data-plan-row].is-selected [data-stop-order]');if(!order)return false;order.value='1';order.dispatchEvent(new Event('input',{bubbles:true}));return order.value==='1';})()"));
            await browser.SetViewportAsync(320, 900, mobile: true);
            Assert.True(await browser.EvaluateBooleanAsync("document.documentElement.scrollWidth <= window.innerWidth"),
                "The completed route plan should still fit the mobile viewport before saving.");
            var mobileSummaryMetrics = await browser.EvaluateStringAsync("JSON.stringify((()=>{const b=document.querySelector('.nr-planning-submit [type=submit]');b?.scrollIntoView({block:'center',behavior:'instant'});const r=b?.getBoundingClientRect();return {button:Boolean(b),top:r?.top,bottom:r?.bottom,height:innerHeight,scrollY, count:document.querySelector('[data-summary-count]')?.innerText,destination:document.querySelector('[data-summary-destination]')?.dataset.confirmed,custody:document.querySelector('[data-summary-custody]')?.dataset.confirmed,selected:document.querySelectorAll('[data-plan-row].is-selected').length,bodyWidth:document.documentElement.scrollWidth,viewport:innerWidth};})())");
            Assert.True(await browser.EvaluateBooleanAsync("(()=>{const b=document.querySelector('.nr-planning-submit [type=submit]');const r=b?.getBoundingClientRect();return Boolean(b&&r&&r.top>=0&&r.bottom<=innerHeight&&document.querySelector('[data-summary-count]')?.innerText==='1'&&document.querySelector('[data-summary-destination]')?.dataset.confirmed==='true'&&document.querySelector('[data-summary-custody]')?.dataset.confirmed==='true');})()"),
                $"The mobile summary and save action should be reachable and reflect verified selected-shipment conditions. {mobileSummaryMetrics}");
            await browser.CaptureScreenshotAsync(ScreenshotPath("planificacion-320x900-resumen-y-guardar.png"));
            await browser.ClickButtonContainingKeyboardAsync("Guardar planificaci\u00F3n");
            await browser.WaitForAsync("document.querySelector('.nr-routes-alert.is-error')?.innerText.includes('No se puede planificar') === true");
            Assert.True(await browser.EvaluateBooleanAsync($"(()=>{{const link=document.querySelector('.nr-routes-alert a');if(!link)return false;const u=new URL(link.href);return u.searchParams.get('fecha')==='{fechaDeReservaOriginal:yyyy-MM-dd}'&&u.searchParams.get('rutaSeleccionadaId')==='{reservaVaciaId:D}'&&link.innerText.includes('Revisar ruta');}})()"),
                "The occupied-resource conflict should identify the existing intentionally empty route and link to its details.");
            Assert.True(await browser.EvaluateBooleanAsync($"document.querySelector('#Fecha')?.value==='{fechaDeReservaOriginal:yyyy-MM-dd}'&&document.querySelector('#RepartidorAccesoId')?.value==='{operatorAccessId:D}'&&document.querySelector('#VehiculoId')?.value==='{sourceVehicleId:D}'&&document.querySelector('[data-plan-row].is-selected [data-stop-order]')?.value==='1'&&document.querySelector('[data-plan-row].is-selected [data-stop-time]')?.value==='17:30'"),
                "A reservation conflict must preserve the selected date, resources, shipment, order and arrival time.");
            await browser.CaptureScreenshotAsync(ScreenshotPath("planificacion-conflicto-reserva-fecha-original.png"));
            var previousSummaryDate = await browser.EvaluateStringAsync("document.querySelector('[data-summary-date]')?.innerText.trim() || ''");
            Assert.True(await browser.EvaluateBooleanAsync($"(()=>{{const d=document.querySelector('#Fecha');if(!d)return false;d.value='{fechaSeleccionada:yyyy-MM-dd}';d.dispatchEvent(new Event('input',{{bubbles:true}}));d.dispatchEvent(new Event('change',{{bubbles:true}}));return d.value==='{fechaSeleccionada:yyyy-MM-dd}';}})()"),
                "The edited departure date should be a real input value before retrying the same plan.");
            var selectedSummaryDate = await browser.EvaluateStringAsync("document.querySelector('[data-summary-date]')?.innerText.trim() || ''");
            Assert.NotEqual(previousSummaryDate, selectedSummaryDate);
            Assert.Contains(fechaSeleccionada.Day.ToString(CultureInfo.InvariantCulture), selectedSummaryDate, StringComparison.Ordinal);
            Assert.Contains(fechaSeleccionada.ToString("MMMM", CultureInfo.GetCultureInfo("es-UY")), selectedSummaryDate, StringComparison.OrdinalIgnoreCase);
            await browser.CaptureScreenshotAsync(ScreenshotPath("planificacion-fecha-editada-y-resumen-actualizado.png"), fullPage: true);
            var fechaCambioMes = new DateOnly(fecha.Year, fecha.Month, DateTime.DaysInMonth(fecha.Year, fecha.Month)).AddDays(1);
            Assert.True(await browser.EvaluateBooleanAsync($"(()=>{{const d=document.querySelector('#Fecha');d.value='{fechaCambioMes:yyyy-MM-dd}';d.dispatchEvent(new Event('input',{{bubbles:true}}));d.dispatchEvent(new Event('change',{{bubbles:true}}));return d.value==='{fechaCambioMes:yyyy-MM-dd}';}})()"),
                "The date control should accept a selection across a month boundary.");
            var summaryAfterMonthChange = await browser.EvaluateStringAsync("document.querySelector('[data-summary-date]')?.innerText.trim() || ''");
            Assert.Contains(fechaCambioMes.Day.ToString(CultureInfo.InvariantCulture), summaryAfterMonthChange, StringComparison.Ordinal);
            Assert.Contains(fechaCambioMes.ToString("MMMM", CultureInfo.GetCultureInfo("es-UY")), summaryAfterMonthChange, StringComparison.OrdinalIgnoreCase);
            Assert.True(await browser.EvaluateBooleanAsync($"(()=>{{const d=document.querySelector('#Fecha');d.value='{fechaSeleccionada:yyyy-MM-dd}';d.dispatchEvent(new Event('input',{{bubbles:true}}));d.dispatchEvent(new Event('change',{{bubbles:true}}));return d.value==='{fechaSeleccionada:yyyy-MM-dd}';}})()"),
                "Restore the requested 30/10 selection after the month-boundary summary check.");
            Assert.Equal(selectedSummaryDate, await browser.EvaluateStringAsync("document.querySelector('[data-summary-date]')?.innerText.trim() || ''"));
            await browser.ClickButtonContainingKeyboardAsync("Guardar planificación");
            await browser.WaitForAsync("location.pathname.toLowerCase() === '/rutas' && document.querySelector('.nr-routes-alert.is-success')?.innerText.includes('Ruta planificada') === true");

            await browser.WaitForAsync($"new URLSearchParams(location.search).get('fecha')==='{fechaSeleccionada:yyyy-MM-dd}'");
            await browser.CaptureScreenshotAsync(ScreenshotPath("operacion-diaria-fecha-seleccionada.png"), fullPage: true);
            fecha = fechaSeleccionada;
            Guid sourceRouteId;
            long sourceRouteVersion;
            await using (var afterBrowserPlan = database.CreateDbContext())
            {
                var route = await afterBrowserPlan.HojasRuta.SingleAsync(x => x.Fecha == fecha && x.RepartidorAccesoId == operatorAccessId);
                Assert.Equal(sourceVehicleId, route.VehiculoId);
                sourceRouteId = route.Id;
                sourceRouteVersion = route.Version;
                var priorDateRoute = await afterBrowserPlan.HojasRuta.SingleAsync(x => x.Id == reservaVaciaId);
                Assert.Equal((fechaDeReservaOriginal, EstadoHojaRuta.Planificada, true),
                    (priorDateRoute.Fecha, priorDateRoute.Estado, priorDateRoute.ReservaRecursosActiva));
                Assert.Equal(EstadoEnvio.AsignadoRuta, (await afterBrowserPlan.Envios.SingleAsync(x => x.Id == shipmentId)).Estado);
                Assert.Equal(EstadoCustodiaBulto.Deposito,
                    (await afterBrowserPlan.Bultos.SingleAsync(x => x.EnvioId == shipmentId)).Custodia);
            }
            using var targetPlan = await api.PostAsJsonAsync("/api/rutas", new
            {
                RepartidorAccesoId = destinationCourierAccessId,
                VehiculoId = targetVehicleJson.RootElement.GetProperty("id").GetGuid(),
                Fecha = fecha,
                Paradas = Array.Empty<object>()
            });
            Assert.Equal(HttpStatusCode.OK, targetPlan.StatusCode);
            using var targetPlanJson = JsonDocument.Parse(await targetPlan.Content.ReadAsStringAsync());
            var targetRouteId = targetPlanJson.RootElement.GetProperty("id").GetGuid();
            var targetRouteVersion = targetPlanJson.RootElement.GetProperty("version").GetInt64();
            using var dispatched = await api.PostAsJsonAsync($"/api/rutas/{sourceRouteId}/despacho",
                new { Version = sourceRouteVersion });
            Assert.Equal(HttpStatusCode.OK, dispatched.StatusCode);

            long shipmentVersion;
            long transitionCountBefore;
            long reassignmentFactsBefore;
            long noticeCountBefore;
            long pushCountBefore;
            await using (var db = database.CreateDbContext())
            {
                var shipment = await db.Envios.SingleAsync(x => x.Id == shipmentId);
                Assert.Equal(EstadoEnvio.AsignadoRuta, shipment.Estado);
                shipmentVersion = shipment.Version;
                Assert.True(shipmentVersion > 0);
                Assert.Equal(EstadoCustodiaBulto.Deposito,
                    (await db.Bultos.SingleAsync(x => x.EnvioId == shipmentId)).Custodia);
                transitionCountBefore = await db.EnvioEventos.CountAsync(x => x.EnvioId == shipmentId && x.Tipo == "TransicionEstado");
                reassignmentFactsBefore = await db.EnvioEventos.CountAsync(x => x.EnvioId == shipmentId
                    && x.Tipo == "Hecho" && x.OrigenTipo == "ReasignacionRuta");
                noticeCountBefore = await db.AvisosReparto.CountAsync(x => x.EnvioId == shipmentId);
                pushCountBefore = await db.OutboxMessages.CountAsync(x => x.EnvioId == shipmentId && x.Destino == OutboxDestino.PushFcm);
            }

            await browser.NavigateAsync($"{backofficeUrl}/rutas?fecha={fecha:yyyy-MM-dd}&pestana=rutas&rutaSeleccionadaId={sourceRouteId:D}");
            await browser.WaitForAsync("document.querySelector('form select[name=destino]') !== null");
            Assert.True(await browser.EvaluateBooleanAsync("new URLSearchParams(location.search).get('fecha') !== null && document.querySelector('#buscar-ruta')?.value === '' && document.querySelector('.nr-route-card.is-selected h3')?.innerText.includes('source courier') === true"),
                "The reference screenshot should show the unfiltered daily route list and the selected source route.");
            await browser.SetViewportAsync(1513, 1039, mobile: false);
            Assert.True(await browser.EvaluateBooleanAsync("document.querySelector('#backofficeNavigation a.active')?.getAttribute('href')==='/rutas'"),
                "Daily operation should reuse the shared Rutas navigation entry.");
            Assert.True(await browser.HasSingleSharedBackofficeShellAsync(), "Daily operation should not introduce a second or alternate Backoffice shell.");
            Assert.Equal(originalShellSignature, await browser.GetSharedShellSignatureAsync());
            await browser.CaptureScreenshotAsync(ScreenshotPath("shell-rutas-y-deposito-1513x1039.png"));
            await browser.CaptureScreenshotAsync(ScreenshotPath("operacion-diaria-1513x1039.png"));
            var tableScrollMetrics = await browser.EvaluateStringAsync("JSON.stringify((()=>{const e=document.querySelector('.nr-stops-table-wrap');return {scrollWidth:e?.scrollWidth,clientWidth:e?.clientWidth,scrollHeight:e?.scrollHeight,clientHeight:e?.clientHeight,overflowX:e&&getComputedStyle(e).overflowX,overflowY:e&&getComputedStyle(e).overflowY};})())");
            Assert.True(await browser.EvaluateBooleanAsync("(()=>{const e=document.querySelector('.nr-stops-table-wrap');return Boolean(e&&e.scrollHeight<=e.clientHeight+1);})()"),
                $"The desktop table must not expose an unnecessary vertical scroller. {tableScrollMetrics}");
            var routeFonts = string.Join(Environment.NewLine, new[]
            {
                $"Route heading: {await browser.GetPlatformFontsAsync(".nr-routes-heading h1")}",
                $"Route card: {await browser.GetPlatformFontsAsync(".nr-route-card h3")}",
                $"Stop identity: {await browser.GetPlatformFontsAsync(".nr-stops-table tbody tr td:nth-child(2)")}",
                $"Stop state: {await browser.GetPlatformFontsAsync(".nr-shipment-state .nr-state-pill")}",
                $"Stop actions: {await browser.GetPlatformFontsAsync(".nr-stops-table tbody tr td:last-child summary")}",
                $"Computed stacks and metrics: {await browser.EvaluateStringAsync("JSON.stringify(['.nr-routes-page','.nr-routes-heading h1','.nr-route-card h3','.nr-stops-table th','.nr-stops-table td','.nr-planning-summary','.nr-summary-kpis dt'].map(s=>{const e=document.querySelector(s);if(!e)return {selector:s,missing:true};const c=getComputedStyle(e);return {selector:s,family:c.fontFamily,size:c.fontSize,weight:c.fontWeight,lineHeight:c.lineHeight};}))") }"
            });
            await File.WriteAllTextAsync(ScreenshotPath("fuentes-renderizadas.txt"), renderedFontEvidence + Environment.NewLine + routeFonts);
            await browser.SetViewportAsync(320, 900, mobile: true);
            Assert.True(await browser.EvaluateBooleanAsync("document.documentElement.scrollWidth <= window.innerWidth"),
                "Backoffice routes horizontally overflow at 320 CSS pixels.");
            Assert.True(await browser.EvaluateBooleanAsync("(()=>{const sidebar=document.querySelector('#backofficeNavigation');return Boolean(document.querySelectorAll('.nr-topbar').length===1&&document.querySelectorAll('#backofficeNavigation').length===1&&sidebar&&!sidebar.classList.contains('show')&&getComputedStyle(sidebar).display==='none'&&document.querySelector('.nr-topbar .nr-menu-toggle')?.getAttribute('aria-expanded')==='false');})()"),
                "The original mobile navigation should start collapsed without duplicating the shell.");
            await browser.CaptureScreenshotAsync(ScreenshotPath("shell-menu-movil-cerrado-320x900.png"));
            await browser.ToggleBackofficeNavigationWithKeyboardAsync(open: true);
            await browser.WaitForAsync("document.querySelector('#backofficeNavigation')?.classList.contains('show')===true");
            Assert.True(await browser.EvaluateBooleanAsync("(()=>{const button=document.querySelector('.nr-topbar .nr-menu-toggle');return Boolean(button?.getAttribute('aria-expanded')==='true'&&document.activeElement===button&&getComputedStyle(document.querySelector('#backofficeNavigation')).display!=='none'&&document.querySelectorAll('.nr-topbar').length===1);})()"),
                "The original mobile menu should open by keyboard, expose its state, and keep focus predictable.");
            await browser.CaptureScreenshotAsync(ScreenshotPath("shell-menu-movil-abierto-320x900.png"));
            await browser.CaptureScreenshotAsync(ScreenshotPath("shell-menu-movil-abierto-completo.png"), fullPage: true);
            await browser.ToggleBackofficeNavigationWithKeyboardAsync(open: false);
            await browser.WaitForAsync("document.querySelector('#backofficeNavigation')?.classList.contains('show')===false");
            var countMetrics = await browser.EvaluateStringAsync("JSON.stringify((()=>{const pill=document.querySelector('#decisiones-operativas .nr-section-heading .nr-count-pill');const style=pill&&getComputedStyle(pill);return {exists:Boolean(pill),className:pill?.className,whiteSpace:style?.whiteSpace,scrollWidth:pill?.scrollWidth,clientWidth:pill?.clientWidth,scrollHeight:pill?.scrollHeight,clientHeight:pill?.clientHeight,rect:pill&&{width:pill.getBoundingClientRect().width,height:pill.getBoundingClientRect().height},text:pill?.innerText};})())");
            Assert.True(await browser.EvaluateBooleanAsync("(()=>{const pill=document.querySelector('#decisiones-operativas .nr-section-heading .nr-count-pill');return Boolean(pill&&getComputedStyle(pill).whiteSpace==='nowrap'&&pill.scrollWidth<=pill.clientWidth+1);})()"),
                $"The operational loss-availability count should remain on one readable line on mobile. {countMetrics}");
            await browser.CaptureScreenshotAsync(ScreenshotPath("operacion-diaria-320x900.png"));
            await browser.CaptureScreenshotAsync(ScreenshotPath("operacion-diaria-320x900-full.png"), fullPage: true);
            Assert.True(await browser.EvaluateBooleanAsync("(()=>{const page=document.querySelector('.nr-routes-page');const family=getComputedStyle(page).fontFamily;const table=document.querySelector('.nr-stops-table');const row=table?.querySelector('tbody tr');const summary=row?.querySelector('td:last-child summary');const rendered=[...document.querySelectorAll('.nr-route-card h3,.nr-stops-table th,.nr-stops-table td,.nr-route-detail-panel button')];return family.includes('sans-serif')&&rendered.every(x=>getComputedStyle(x).fontFamily===family)&&Boolean(row&&row.innerText.includes('Versión del envío')&&row.querySelector('.nr-stop-shipment-id')&&row.querySelector('.nr-shipment-state .nr-state-pill')&&row.querySelector('.nr-package-chips .nr-state-pill')&&summary?.innerText.includes('Reasignar')&&summary.closest('details')?.querySelector('form select[name=destino]'));})()"),
                "Daily operation should expose one semantic responsive stop row with shipment identity, state, custody and contextual actions.");
            Assert.True(await browser.EvaluateBooleanAsync("(()=>{const row=document.querySelector('.nr-stops-table tbody tr');const wrap=document.querySelector('.nr-stops-table-wrap');const cells=[...row.cells];const bounds=e=>{const r=e.getBoundingClientRect();return r.left>=0&&r.right<=innerWidth+1};return Boolean(row&&wrap&&document.documentElement.scrollWidth<=innerWidth&&wrap.scrollWidth<=wrap.clientWidth+1&&bounds(row)&&cells.every(bounds)&&row.innerText.includes('DEC07 browser reassignment recipient')&&row.innerText.includes('Envío')&&row.innerText.includes('17:30')&&row.innerText.includes('Asignado a ruta')&&row.innerText.includes('En depósito')&&row.innerText.includes('Reasignar')&&row.innerText.includes('Confirmar extravío'));})()"),
                "At 320 CSS pixels, each stop card must keep recipient, shipment ID, order/time, shipment state, physical custody and authorized actions together without horizontal scrolling.");
            await browser.CaptureScreenshotAsync(ScreenshotPath("operacion-diaria-320x900-parada-completa.png"), fullPage: true);
            Assert.True(await browser.ToggleFirstRouteActionWithSpaceAsync(),
                "Contextual route actions should open through the keyboard on mobile.");
            Assert.True(await browser.EvaluateBooleanAsync("(()=>{const d=[...document.querySelectorAll('.nr-stops-table tbody tr details')].find(x=>x.innerText.includes('Reasignar'));if(!d?.open)return false;const f=d.querySelector('form'),s=f?.querySelector('select[name=destino]'),h=f?.querySelector('input[name=hora]'),m=f?.querySelector('textarea[name=motivo]');if(!s||!h||!m)return false;s.value=[...s.options].find(o=>o.value)?.value||'';h.value='17:30';m.value='Reasignación de prueba visual';return s.value!==''&&f.checkValidity()&&document.documentElement.scrollWidth<=innerWidth&&[s,h,m].every(e=>{const r=e.getBoundingClientRect();return r.left>=0&&r.right<=innerWidth+1;});})()"),
                "The existing reassignment form should remain keyboard reachable, valid and within a 320 CSS-pixel viewport.");
            await browser.CaptureScreenshotAsync(ScreenshotPath("operacion-diaria-320x900-reasignar-abierta.png"), fullPage: true);
            await browser.NavigateAsync($"{backofficeUrl}/rutas?fecha={fecha:yyyy-MM-dd}&pestana=rutas&rutaSeleccionadaId={sourceRouteId:D}");
            await browser.WaitForAsync("document.querySelector('.nr-stops-table tbody tr details form textarea[name=MotivoExtravio]') !== null");
            await browser.SetViewportAsync(320, 900, mobile: true);
            Assert.True(await browser.EvaluateBooleanAsync("(()=>{const d=[...document.querySelectorAll('.nr-stops-table tbody tr details')].find(x=>x.innerText.includes('Confirmar extravío'));if(!d)return false;d.open=true;const f=d.querySelector('form'),m=f.querySelector('textarea[name=MotivoExtravio]');m.value='Inspección visual, sin enviar';return !f.checkValidity()&&document.documentElement.scrollWidth<=innerWidth&&[...f.querySelectorAll('textarea,input,button')].every(e=>{const r=e.getBoundingClientRect();return r.left>=0&&r.right<=innerWidth+1;});})()"),
                "The unsubmitted loss confirmation form must expose its required validation and fit the narrow viewport.");
            await browser.CaptureScreenshotAsync(ScreenshotPath("operacion-diaria-320x900-extravio-abierto.png"), fullPage: true);
            await browser.SetViewportAsync(1513, 1039, mobile: false);
            Assert.True(await browser.EvaluateBooleanAsync("(()=>{const a=document.querySelector('a[aria-label=\"Día siguiente\"]');if(!a)return false;a.click();return true;})()"));
            await browser.WaitForAsync($"new URLSearchParams(location.search).get('fecha') === '{fecha.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}'");
            await browser.NavigateAsync($"{backofficeUrl}/rutas?fecha={fecha:yyyy-MM-dd}&pestana=rutas&rutaSeleccionadaId={sourceRouteId:D}");
            await browser.WaitForAsync("document.querySelector('form select[name=destino]') !== null");

            await browser.NavigateAsync($"{backofficeUrl}/rutas?fecha={fecha:yyyy-MM-dd}&pestana=rutas&buscarRuta=DEC07&filtroRuta=Todas&rutaSeleccionadaId={sourceRouteId:D}");
            await browser.WaitForAsync("document.querySelector('form select[name=destino]') !== null");
            Assert.True(await browser.EvaluateBooleanAsync("new URLSearchParams(location.search).get('fecha') !== null && document.querySelector('#buscar-ruta')?.value === 'DEC07' && document.querySelector('.nr-route-card.is-selected h3')?.innerText.includes('source courier') === true"),
                "The selected route, search and date context should be rendered from the current URL.");
            await browser.ClickLinkContainingKeyboardAsync("Despachadas");
            await browser.WaitForAsync("new URLSearchParams(location.search).get('filtroRuta') === 'Despachada' && document.querySelector('.nr-route-list-panel > header span')?.innerText === '1 de 2'");
            Assert.True(await browser.EvaluateBooleanAsync("document.querySelector('#buscar-ruta')?.value === 'DEC07' && document.querySelector('.nr-route-card.is-selected h3')?.innerText.includes('source courier') === true"),
                "Changing the route filter should retain its search and selected-route context.");
            await browser.ClickLinkContainingKeyboardAsync("Todas");
            await browser.WaitForAsync("new URLSearchParams(location.search).get('filtroRuta') === 'Todas' && document.querySelector('.nr-route-list-panel > header span')?.innerText === '2 de 2'");
            await browser.SetViewportAsync(320, 900, mobile: true);
            Assert.True(await browser.EvaluateBooleanAsync("document.documentElement.scrollWidth <= window.innerWidth"),
                "Backoffice routes horizontally overflow at 320 CSS pixels.");
            await browser.SetViewportAsync(1513, 1039, mobile: false);
            Assert.True(await browser.EvaluateBooleanAsync($"(()=>{{const a=document.querySelector('.nr-route-card[href*=\\\"{targetRouteId:D}\\\"]');if(!a)return false;a.click();return true;}})()"));
            await browser.WaitForAsync($"new URLSearchParams(location.search).get('rutaSeleccionadaId') === '{targetRouteId:D}'");
            Assert.True(await browser.EvaluateBooleanAsync($"document.querySelector('#buscar-ruta')?.value === 'DEC07' && document.querySelector('.nr-route-card.is-selected h3')?.innerText.includes('destination courier') === true"),
                "Selecting a route should preserve the search/date context and update the detail panel.");
            Assert.True(await browser.EvaluateBooleanAsync($"(()=>{{const a=document.querySelector('.nr-route-card[href*=\\\"{sourceRouteId:D}\\\"]');if(!a)return false;a.click();return true;}})()"));
            await browser.WaitForAsync($"new URLSearchParams(location.search).get('rutaSeleccionadaId') === '{sourceRouteId:D}'");
            await browser.EvaluateBooleanAsync("(()=>{const s=document.querySelector('form select[name=destino]');if(!s)return false;s.closest('details').open=true;return true;})()");
            await browser.VerifyKeyboardFocusAsync();

            var routeSelection = $"{targetRouteId:D}:{targetRouteVersion.ToString(CultureInfo.InvariantCulture)}";
            await browser.SetViewportAsync(320, 900, mobile: true);
            Assert.True(await browser.EvaluateBooleanAsync("document.documentElement.scrollWidth <= window.innerWidth"),
                "The reassignment conflict recovery path should remain usable at 320 CSS pixels.");
            var setForm = (long version) => $"(()=>{{const s=document.querySelector('form select[name=destino]');if(!s)return false;const f=s.form;const v=f.querySelector('input[name=versionEnvio]');const h=f.querySelector('input[name=hora]');const m=f.querySelector('textarea[name=motivo]');s.value={JsonSerializer.Serialize(routeSelection)};s.dispatchEvent(new Event('change',{{bubbles:true}}));v.value={JsonSerializer.Serialize(version.ToString(CultureInfo.InvariantCulture))};h.value='18:00';h.dispatchEvent(new Event('input',{{bubbles:true}}));m.value='Reasignación confirmada desde Backoffice';m.dispatchEvent(new Event('input',{{bubbles:true}}));return s.value!==''&&Boolean(v.value)&&Boolean(h.value)&&Boolean(m.value);}})()";
            Assert.True(await browser.EvaluateBooleanAsync(setForm(shipmentVersion - 1)));
            await browser.ClickButtonContainingKeyboardAsync("Reasignar a esa ruta");
            await browser.WaitForAsync("document.querySelector('.alert-danger') !== null");
            Assert.Contains("Solo se reasignan envíos asignados", await browser.EvaluateStringAsync("document.querySelector('.alert-danger')?.innerText || ''"),
                StringComparison.OrdinalIgnoreCase);
            await browser.CaptureScreenshotAsync(ScreenshotPath("operacion-diaria-320x900-conflicto-version.png"), fullPage: true);
            var refreshedVersion = await browser.EvaluateStringAsync("document.querySelector('form select[name=destino]')?.form.querySelector('input[name=versionEnvio]')?.value || ''");
            Assert.Equal(shipmentVersion.ToString(CultureInfo.InvariantCulture), refreshedVersion);
            await using (var afterConflict = database.CreateDbContext())
            {
                var shipment = await afterConflict.Envios.SingleAsync(x => x.Id == shipmentId);
                Assert.Equal((EstadoEnvio.AsignadoRuta, shipmentVersion), (shipment.Estado, shipment.Version));
                Assert.Equal(EstadoCustodiaBulto.Deposito,
                    (await afterConflict.Bultos.SingleAsync(x => x.EnvioId == shipmentId)).Custodia);
                Assert.Empty(await afterConflict.ParadasRuta.Where(x => x.EnvioId == shipmentId && x.RutaId == targetRouteId).ToArrayAsync());
                Assert.Equal(reassignmentFactsBefore, await afterConflict.EnvioEventos.CountAsync(x => x.EnvioId == shipmentId
                    && x.Tipo == "Hecho" && x.OrigenTipo == "ReasignacionRuta"));
                Assert.Equal(noticeCountBefore, await afterConflict.AvisosReparto.CountAsync(x => x.EnvioId == shipmentId));
                Assert.Equal(pushCountBefore, await afterConflict.OutboxMessages.CountAsync(x => x.EnvioId == shipmentId && x.Destino == OutboxDestino.PushFcm));
            }

            await browser.EvaluateBooleanAsync("(()=>{const s=document.querySelector('form select[name=destino]');if(!s)return false;s.closest('details').open=true;return true;})()");
            Assert.True(await browser.EvaluateBooleanAsync(setForm(shipmentVersion)));
            var submittedTwice = await browser.EvaluateBooleanAsync("(()=>{const s=document.querySelector('form select[name=destino]');if(!s)return false;const f=s.form;const b=f.querySelector('button');if(!b)return false;b.focus();b.click();f.requestSubmit();return f.dataset.submitted==='true';})()");
            Assert.True(submittedTwice, "The form must mark its first submit and suppress a repeated submit.");
            await browser.WaitForAsync("document.querySelector('.nr-routes-alert.is-success')?.innerText.includes('custodia física no cambió') === true");

            await browser.NavigateAsync($"{backofficeUrl}/rutas?fecha={fecha:yyyy-MM-dd}&pestana=deposito");
            await browser.WaitForAsync($"document.querySelector('.nr-receipt-card input[name=envioId][value=\\\"{receiptShipmentId:D}\\\"]') !== null");
            await browser.ClickReceiptForShipmentKeyboardAsync(receiptShipmentId);
            await browser.WaitForAsync("document.querySelector('.nr-routes-alert.is-success')?.innerText.includes('Recepción de los bultos confirmada en depósito') === true");
            await using (var afterBrowserReceipt = database.CreateDbContext())
            {
                var receivedShipment = await afterBrowserReceipt.Envios.SingleAsync(x => x.Id == receiptShipmentId);
                Assert.Equal(EstadoEnvio.EnDeposito, receivedShipment.Estado);
                Assert.Equal(EstadoCustodiaBulto.Deposito,
                    (await afterBrowserReceipt.Bultos.SingleAsync(x => x.EnvioId == receiptShipmentId)).Custodia);
            }

            using var staleRetry = await api.PostAsJsonAsync($"/api/envios/{shipmentId}/reasignacion", new
            {
                RutaDestinoId = targetRouteId,
                VersionEnvio = shipmentVersion,
                VersionRutaDestino = targetRouteVersion,
                LlegadaEstimadaUtc = new DateTimeOffset(fecha.ToDateTime(new TimeOnly(18, 0)), TimeSpan.Zero),
                Motivo = "Reasignación confirmada desde Backoffice"
            });
            Assert.Equal(HttpStatusCode.Conflict, staleRetry.StatusCode);

            await using (var afterSuccess = database.CreateDbContext())
            {
                var shipment = await afterSuccess.Envios.SingleAsync(x => x.Id == shipmentId);
                Assert.Equal(EstadoEnvio.AsignadoRuta, shipment.Estado);
                Assert.Equal(shipmentVersion + 1, shipment.Version);
                Assert.Equal(EstadoCustodiaBulto.Deposito,
                    (await afterSuccess.Bultos.SingleAsync(x => x.EnvioId == shipmentId)).Custodia);
                Assert.False((await afterSuccess.ParadasRuta.SingleAsync(x => x.EnvioId == shipmentId && x.RutaId == sourceRouteId)).Activa);
                Assert.True((await afterSuccess.ParadasRuta.SingleAsync(x => x.EnvioId == shipmentId && x.RutaId == targetRouteId)).Activa);
                Assert.Equal(transitionCountBefore, await afterSuccess.EnvioEventos.CountAsync(x => x.EnvioId == shipmentId && x.Tipo == "TransicionEstado"));
                Assert.Equal(reassignmentFactsBefore + 1, await afterSuccess.EnvioEventos.CountAsync(x => x.EnvioId == shipmentId
                    && x.Tipo == "Hecho" && x.OrigenTipo == "ReasignacionRuta"
                    && x.VersionAnterior == shipmentVersion && x.VersionNueva == shipmentVersion + 1));
                Assert.Equal(noticeCountBefore + 1, await afterSuccess.AvisosReparto.CountAsync(x => x.EnvioId == shipmentId));
                Assert.Equal(pushCountBefore + 1, await afterSuccess.OutboxMessages.CountAsync(x => x.EnvioId == shipmentId && x.Destino == OutboxDestino.PushFcm));
            }
            await browser.VerifyKeyboardFocusAsync();
            output.WriteLine($"Eligible reassignment, stale-version conflict, suppressed double submit and stale retry verified in Backoffice Chromium/Kestrel with isolated restricted-API PostgreSQL. Screenshots were temporary under {tempRoot}.");
        }
        finally
        {
            if (browser is not null) await browser.DisposeAsync();
            if (backofficeProcess is not null) await backofficeProcess.DisposeAsync();
            if (apiProcess is not null) await apiProcess.DisposeAsync();
            for (var attempt = 0; Directory.Exists(tempRoot) && attempt < 10; attempt++)
            {
                try { Directory.Delete(tempRoot, recursive: true); break; }
                catch (IOException) { if (attempt < 9) await Task.Delay(200); }
            }
            if (Directory.Exists(tempRoot))
                output.WriteLine($"Chromium still held its isolated temporary profile; retained for cleanup after process exit: {tempRoot}");
        }
    }

    [Fact]
    public async Task BackofficeBrowserIniciaDecisionOperativaConConflictoYReintentoSeguro()
    {
        var database = await postgres.CreateDatabaseAsync();
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nexoruta-dec05-browser-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        KestrelProcess? apiProcess = null;
        KestrelProcess? backofficeProcess = null;
        ChromiumPage? browser = null;
        ChromiumPage? adminBrowser = null;
        try
        {
            const string commerceEmail = "dec05-browser-commerce@example.test";
            const string operatorEmail = "dec05-browser-operator@example.test";
            var seedSettings = AuthenticationTestData.Settings();
            seedSettings["AccesoInicial:UsuarioEmail"] = commerceEmail;
            seedSettings["AccesoInicial:OperadorUsuarioEmail"] = operatorEmail;
            seedSettings["DataProtection:KeysDirectory"] = Path.Combine(tempRoot, "api-keys");
            using (var bootstrap = new IntegrationApiFactory(database, settings: seedSettings))
            using (bootstrap.CreateHttpClient()) { }

            Guid operatorId;
            Guid operatorAccessId;
            await using (var db = database.CreateDbContext())
            {
                var operatorAccess = await (from access in db.AccesosUsuario
                    join user in db.Usuarios on access.UsuarioId equals user.Id
                    where user.Email == operatorEmail
                    select access).SingleAsync();
                operatorId = operatorAccess.OperadorId!.Value;
                operatorAccessId = operatorAccess.Id;
                var rules = new EfReglasOperativasRepository(db);
                await rules.CrearBorradorAsync(operatorId, operatorAccess.UsuarioId);
                await rules.EditarBorradorAsync(operatorId, operatorAccess.UsuarioId, 0,
                    new(1, 30, PoliticaDevolucionAlAgotarIntentos.DecisionOperativa, 2880, 1440, 60,
                        [TipoPruebaEntrega.NombreReceptor], [new("AUSENTE", "Ausente", true)]));
                await rules.PublicarBorradorAsync(operatorId, operatorAccess.UsuarioId, 1);
            }

            var runtimeConnection = await CreateRestrictedApiConnectionAsync(database);
            var apiPort = FindFreePort();
            var backofficePort = FindFreePort();
            var apiUrl = $"http://127.0.0.1:{apiPort}";
            var backofficeUrl = $"http://127.0.0.1:{backofficePort}";
            var root = FindWorkspaceRoot();
            var apiDll = Path.Combine(root, "backend", "src", "NexoRuta.Api", "bin", "Debug", "net10.0", "NexoRuta.Api.dll");
            var backofficeDll = Path.Combine(root, "frontend", "src", "NexoRuta.Backoffice", "bin", "Debug", "net10.0", "NexoRuta.Backoffice.dll");
            Assert.True(File.Exists(apiDll), "Build the API before the isolated browser check.");
            Assert.True(File.Exists(backofficeDll), "Build Backoffice before the isolated browser check.");

            apiProcess = StartDotnet(apiDll, Path.GetDirectoryName(apiDll)!, apiUrl, new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["ConnectionStrings__Postgres"] = runtimeConnection,
                ["DatabaseInitialization__ApplyMigrations"] = "false",
                ["DatabaseInitialization__SeedData"] = "false",
                ["OpenTelemetry__Enabled"] = "false",
                ["Jwt__Issuer"] = AuthenticationTestData.Issuer,
                ["Jwt__Audience"] = AuthenticationTestData.Audience,
                ["Jwt__SigningKeyBase64"] = AuthenticationTestData.SigningKey,
                ["DataProtection__KeysDirectory"] = Path.Combine(tempRoot, "api-keys"),
                ["Tracking__PublicBaseUrl"] = "https://tracking.example.test",
                ["PasswordRecovery__ResetPageUrl"] = "https://commerce.example.test/restablecer-contrasena",
                ["PasswordRecovery__TokenLifespanSeconds"] = "900",
                ["Email__Smtp__Host"] = "mailpit.integration.test",
                ["Email__Smtp__Port"] = "1025",
                ["Email__Smtp__UseSsl"] = "false",
                ["Email__Smtp__FromAddress"] = "noreply@example.test",
                ["Email__Smtp__FromName"] = "NexoRuta integration"
            });
            await WaitForHttpAsync(apiProcess.Process, $"{apiUrl}/health/live");
            backofficeProcess = StartDotnet(backofficeDll, Path.GetDirectoryName(backofficeDll)!, backofficeUrl,
                new Dictionary<string, string?>
                {
                    ["ASPNETCORE_ENVIRONMENT"] = "Development",
                    ["Api__BaseAddress"] = apiUrl + "/",
                    ["DataProtection__KeysDirectory"] = Path.Combine(tempRoot, "backoffice-keys")
                });
            await WaitForHttpAsync(backofficeProcess.Process, $"{backofficeUrl}/health/ready");

            using var api = new HttpClient { BaseAddress = new Uri(apiUrl), Timeout = TimeSpan.FromSeconds(45) };
            var commerceToken = await LoginAsync(api, commerceEmail, AuthenticationTestData.Password);
            api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", commerceToken);
            using var shipmentResponse = await api.PostAsJsonAsync("/api/envios", new
            {
                OperadorId = operatorId, DestinatarioNombre = "DEC05 browser recipient",
                DestinatarioEmail = "dec05-recipient@example.test", Direccion = "18 de Julio 123",
                CodigoBulto = "DEC05-BROWSER", PesoGramos = 500m, LargoCentimetros = 20m,
                AnchoCentimetros = 15m, AltoCentimetros = 10m
            });
            Assert.Equal(HttpStatusCode.Created, shipmentResponse.StatusCode);
            using var shipmentJson = JsonDocument.Parse(await shipmentResponse.Content.ReadAsStringAsync());
            var shipmentId = shipmentJson.RootElement.GetProperty("id").GetGuid();
            using var destination = await api.PutAsJsonAsync($"/api/envios/{shipmentId}/destino",
                new { Version = 0, Latitud = -34.9, Longitud = -56.2 });
            Assert.Equal(HttpStatusCode.NoContent, destination.StatusCode);

            var operatorToken = await LoginAsync(api, operatorEmail, AuthenticationTestData.Password);
            api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", operatorToken);
            using var routeRules = await api.PutAsJsonAsync("/api/reparto/configuracion", new
            {
                Version = 0, ZonaHoraria = "America/Montevideo", MaximoParadas = 30,
                RequiereFotoIntento = false, BloquearDiscrepanciasCarga = true
            });
            Assert.Equal(HttpStatusCode.OK, routeRules.StatusCode);
            using var courier = await api.PutAsJsonAsync($"/api/repartidores/{operatorAccessId}",
                new { Nombre = "DEC05 browser courier", Activo = true });
            Assert.Equal(HttpStatusCode.OK, courier.StatusCode);
            using var vehicleResponse = await api.PutAsJsonAsync("/api/reparto/vehiculos", new
            {
                Matricula = "DEC505", PesoMaximoGramos = 10000000m, VolumenMaximoCm3 = 100000000000m, Activo = true
            });
            Assert.Equal(HttpStatusCode.OK, vehicleResponse.StatusCode);
            using var vehicleJson = JsonDocument.Parse(await vehicleResponse.Content.ReadAsStringAsync());
            var vehicleId = vehicleJson.RootElement.GetProperty("id").GetGuid();
            using var received = await api.PostAsJsonAsync($"/api/deposito/envios/{shipmentId}/recepcion", new { Version = 0 });
            Assert.Equal(HttpStatusCode.NoContent, received.StatusCode);

            var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
                TimeZoneInfo.FindSystemTimeZoneById("America/Montevideo")).DateTime);
            var eta = new DateTimeOffset(date.ToDateTime(new TimeOnly(15, 0)), TimeSpan.Zero);
            using var planned = await api.PostAsJsonAsync("/api/rutas", new
            {
                RepartidorAccesoId = operatorAccessId, VehiculoId = vehicleId, Fecha = date,
                Paradas = new[] { new { EnvioId = shipmentId, LlegadaEstimadaUtc = eta } }
            });
            Assert.Equal(HttpStatusCode.OK, planned.StatusCode);
            using var plannedJson = JsonDocument.Parse(await planned.Content.ReadAsStringAsync());
            var routeId = plannedJson.RootElement.GetProperty("id").GetGuid();
            var routeVersion = plannedJson.RootElement.GetProperty("version").GetInt64();
            using var dispatched = await api.PostAsJsonAsync($"/api/rutas/{routeId}/despacho", new { Version = routeVersion });
            Assert.Equal(HttpStatusCode.OK, dispatched.StatusCode);
            using var routes = await api.GetAsync($"/api/mobile/rutas?fecha={date:yyyy-MM-dd}");
            using var routesJson = JsonDocument.Parse(await routes.Content.ReadAsStringAsync());
            var route = routesJson.RootElement.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == routeId);
            var shipmentVersion = route.GetProperty("paradas")[0].GetProperty("envioVersion").GetInt64();
            var parcelId = route.GetProperty("paradas")[0].GetProperty("bultos")[0].GetProperty("id").GetGuid();
            var jornadaId = route.GetProperty("jornadaId").GetGuid();
            async Task<string> MobileAsync(long sequence, string operation, long? version = null, object? payload = null)
            {
                using var response = await api.PostAsJsonAsync($"/api/mobile/jornadas/{jornadaId}/operaciones", new
                {
                    Id = Guid.NewGuid(), Secuencia = sequence, Tipo = operation,
                    EnvioId = operation == "IntentoFallido" ? shipmentId : (Guid?)null,
                    VersionEnvio = operation == "IntentoFallido" ? version : null,
                    OcurridaEn = eta, Datos = payload ?? new { }
                });
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                return result.RootElement.GetProperty("resultado").GetString()!;
            }
            Assert.Equal("Aplicada", await MobileAsync(1, "InicioJornada"));
            using (var load = await api.PostAsJsonAsync($"/api/mobile/jornadas/{jornadaId}/operaciones", new
            {
                Id = Guid.NewGuid(), Secuencia = 2, Tipo = "CargarBulto", EnvioId = shipmentId,
                VersionEnvio = shipmentVersion, OcurridaEn = eta, Datos = new { BultoId = parcelId }
            }))
            {
                Assert.Equal(HttpStatusCode.OK, load.StatusCode);
                using var result = JsonDocument.Parse(await load.Content.ReadAsStringAsync());
                Assert.Equal("Aplicada", result.RootElement.GetProperty("resultado").GetString());
            }
            Assert.Equal("Aplicada", await MobileAsync(3, "ConfirmarCarga"));
            Assert.Equal("Aplicada", await MobileAsync(4, "IntentoFallido", shipmentVersion + 1, new { Motivo = "AUSENTE" }));

            const string dispatcherEmail = "dec06-browser-dispatcher@example.test";
            Guid dispatcherAccessId;
            using (var rolesResponse = await api.GetAsync("/api/roles/asignables"))
            {
                Assert.Equal(HttpStatusCode.OK, rolesResponse.StatusCode);
                using var rolesJson = JsonDocument.Parse(await rolesResponse.Content.ReadAsStringAsync());
                var dispatcherRoleId = rolesJson.RootElement.EnumerateArray()
                    .Single(role => role.GetProperty("codigo").GetString() == "despachador_operador")
                    .GetProperty("id").GetGuid();
                using var assignedDispatcher = await api.PostAsJsonAsync("/api/usuarios", new
                {
                    Email = dispatcherEmail, Password = AuthenticationTestData.Password, RolId = dispatcherRoleId
                });
                Assert.Equal(HttpStatusCode.Created, assignedDispatcher.StatusCode);
            }
            await using (var dispatcherAccess = database.CreateDbContext())
                dispatcherAccessId = await (from access in dispatcherAccess.AccesosUsuario
                    join user in dispatcherAccess.Usuarios on access.UsuarioId equals user.Id
                    where user.Email == dispatcherEmail
                    select access.Id).SingleAsync();

            var chromium = FindChromium();
            Assert.True(File.Exists(chromium), "A cached Chromium executable is required for this isolated browser check.");
            browser = await ChromiumPage.StartAsync(chromium, Path.Combine(tempRoot, "chrome-profile"), backofficeUrl + "/ingresar");
            await browser.LoginAsync(dispatcherEmail, AuthenticationTestData.Password, "/");
            await browser.NavigateAsync($"{backofficeUrl}/rutas?fecha={date:yyyy-MM-dd}");
            await browser.WaitForAsync("document.body.innerText.includes('No se puede reprogramar ni realizar otra tentativa')");
            Assert.True(await browser.EvaluateBooleanAsync("document.querySelector('textarea[name=MotivoDevolucion][maxlength=\\\"500\\\"]') !== null"));
            Assert.True(await browser.EvaluateBooleanAsync("document.querySelector('input[name=ConfirmarDevolucion][required]') !== null"));
            var initialCommand = await browser.EvaluateStringAsync("document.querySelector('input[name=ComandoId]')?.value || ''");
            Assert.False(string.IsNullOrWhiteSpace(initialCommand));
            await browser.FillReturnAndSetStaleVersionAsync("Control del despachador");
            await browser.ClickButtonContainingAsync("Iniciar devolución");
            await browser.WaitForAsync("document.querySelector('.alert-danger')?.innerText.includes('cambió') === true");
            Assert.Equal("Control del despachador", await browser.EvaluateStringAsync("document.querySelector('textarea[name=MotivoDevolucion]')?.value || ''"));
            Assert.Equal(initialCommand, await browser.EvaluateStringAsync("document.querySelector('input[name=ComandoId]')?.value || ''"));
            Assert.NotEqual("0", await browser.EvaluateStringAsync("document.querySelector('input[name=VersionEnvio]')?.value || ''"));

            await browser.SetViewportAsync(1280, 900, mobile: false);
            await browser.CaptureScreenshotAsync(Path.Combine(tempRoot, "desktop.png"));
            await browser.SetViewportAsync(320, 900, mobile: true);
            Assert.True(await browser.EvaluateBooleanAsync("document.documentElement.scrollWidth <= window.innerWidth"),
                "Backoffice routes horizontally overflow at 320 CSS pixels.");
            await browser.CaptureScreenshotAsync(Path.Combine(tempRoot, "mobile.png"));
            await browser.VerifyKeyboardFocusAsync();
            await browser.ClickButtonContainingAsync("Iniciar devolución");
            await browser.WaitForAsync("document.querySelector('.alert-success')?.innerText.includes('Devolución iniciada') === true");

            await using var verify = database.CreateDbContext();
            var shipment = await verify.Envios.SingleAsync(x => x.Id == shipmentId);
            Assert.Equal("EnDevolucion", shipment.Estado.ToString());
            Assert.Equal(EstadoCustodiaBulto.Repartidor, (await verify.Bultos.SingleAsync(x => x.EnvioId == shipmentId)).Custodia);
            var decision = await verify.DecisionesDevolucionEnvio.SingleAsync(x => x.EnvioId == shipmentId);
            Assert.Equal("Control del despachador", decision.Motivo);
            Assert.Equal(dispatcherAccessId, decision.AccesoId);
            Assert.Equal(1, await verify.EnvioEventos.CountAsync(x => x.EnvioId == shipmentId
                && x.EstadoAnterior == "NoEntregado" && x.EstadoNuevo == "EnDevolucion"));
            var versionBeforeLoss = shipment.Version;

            adminBrowser = await ChromiumPage.StartAsync(chromium, Path.Combine(tempRoot, "chrome-admin-profile"), backofficeUrl + "/ingresar");
            await adminBrowser.LoginAsync(operatorEmail, AuthenticationTestData.Password, "/");
            await adminBrowser.NavigateAsync($"{backofficeUrl}/rutas?fecha={date:yyyy-MM-dd}");
            await adminBrowser.WaitForAsync("document.querySelector('form[action*=\\\"ConfirmarExtravio\\\"]') !== null");
            Assert.True(await adminBrowser.EvaluateBooleanAsync(
                "document.querySelector('form[action*=\\\"ConfirmarExtravio\\\"] textarea[name=MotivoExtravio][required]') !== null"));
            await adminBrowser.DisposeAsync();
            adminBrowser = null;

            await browser.NavigateAsync($"{backofficeUrl}/rutas?fecha={date:yyyy-MM-dd}");
            await browser.WaitForAsync("document.body.innerText.includes('Confirmar extravío')");
            Assert.True(await browser.EvaluateBooleanAsync("document.querySelector('form[action*=\\\"ConfirmarExtravio\\\"] textarea[name=MotivoExtravio][maxlength=\\\"500\\\"][required]') !== null"));
            Assert.True(await browser.EvaluateBooleanAsync("document.querySelector('form[action*=\\\"ConfirmarExtravio\\\"] input[name=ConfirmarExtravio][required]') !== null"));
            var rejectedCommand = await browser.EvaluateStringAsync("document.querySelector('form[action*=\\\"ConfirmarExtravio\\\"] input[name=ComandoId]')?.value || ''");
            Assert.False(string.IsNullOrWhiteSpace(rejectedCommand));
            await browser.FillLossAndSetStaleVersionAsync("Control de extravío");
            await browser.ClickButtonContainingAsync("Confirmar extravío");
            await browser.WaitForAsync("document.querySelector('.alert-danger')?.innerText.includes('Se actualizó el estado') === true");
            Assert.Equal("Control de extravío", await browser.EvaluateStringAsync("document.querySelector('form[action*=\\\"ConfirmarExtravio\\\"] textarea[name=MotivoExtravio]')?.value || ''"));
            var refreshedVersion = await browser.EvaluateStringAsync("document.querySelector('form[action*=\\\"ConfirmarExtravio\\\"] input[name=VersionEnvio]')?.value || ''");
            var retryCommand = await browser.EvaluateStringAsync("document.querySelector('form[action*=\\\"ConfirmarExtravio\\\"] input[name=ComandoId]')?.value || ''");
            Assert.Equal(versionBeforeLoss.ToString(System.Globalization.CultureInfo.InvariantCulture), refreshedVersion);
            Assert.NotEqual(rejectedCommand, retryCommand);

            await browser.SetViewportAsync(1280, 900, mobile: false);
            await browser.CaptureScreenshotAsync(Path.Combine(tempRoot, "loss-desktop.png"));
            await browser.SetViewportAsync(320, 900, mobile: true);
            Assert.True(await browser.EvaluateBooleanAsync("document.documentElement.scrollWidth <= window.innerWidth"),
                "Backoffice loss confirmation horizontally overflows at 320 CSS pixels.");
            await browser.VerifyKeyboardFocusAsync();
            await browser.FillLossAsync("Control de extravío");
            Assert.True(await browser.EvaluateBooleanAsync("(()=>{document.querySelector('.alert-danger')?.remove();return true;})()"));
            await browser.ClickButtonContainingKeyboardAsync("Confirmar extravío");
            await browser.WaitForAsync("document.querySelector('.alert-success') !== null || document.querySelector('.alert-danger') !== null");
            var lossAlert = await browser.EvaluateStringAsync("document.querySelector('.alert-success, .alert-danger')?.innerText || 'No result alert'");
            Assert.Contains("Extravío confirmado", lossAlert);
            await browser.CaptureScreenshotAsync(Path.Combine(tempRoot, "loss-mobile.png"));

            await using (var verifyLoss = database.CreateDbContext())
            {
                var shipmentAfterLoss = await verifyLoss.Envios.SingleAsync(x => x.Id == shipmentId);
                Assert.Equal("Extraviado", shipmentAfterLoss.Estado.ToString());
                Assert.Equal(EstadoCustodiaBulto.Repartidor, (await verifyLoss.Bultos.SingleAsync(x => x.EnvioId == shipmentId)).Custodia);
                Assert.Single(await verifyLoss.ConfirmacionesExtravioEnvio.Where(x => x.EnvioId == shipmentId
                    && x.Motivo == "Control de extravío" && x.AccesoId == dispatcherAccessId).ToArrayAsync());
                Assert.Equal(1, await verifyLoss.EnvioEventos.CountAsync(x => x.EnvioId == shipmentId
                    && x.EstadoAnterior == "EnDevolucion" && x.EstadoNuevo == "Extraviado"));
            }
            Assert.False(await browser.EvaluateBooleanAsync($"document.querySelector('input[name=ExtravioEnvioId][value=\\\"{shipmentId}\\\"]') !== null"));
            output.WriteLine($"Backoffice Chromium desktop/mobile DEC-05 and DEC-06 flows verified against local Kestrel and isolated restricted-API PostgreSQL, including protected administrator and dispatcher action visibility, dispatcher submission, stale-version conflict, keyboard submission, and terminal-state action removal. Screenshots were temporary under {tempRoot}.");
        }
        finally
        {
            if (adminBrowser is not null) await adminBrowser.DisposeAsync();
            if (browser is not null) await browser.DisposeAsync();
            if (backofficeProcess is not null) await backofficeProcess.DisposeAsync();
            if (apiProcess is not null) await apiProcess.DisposeAsync();
            if (Directory.Exists(tempRoot) && Path.GetDirectoryName(Path.GetFullPath(tempRoot)) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar))
            {
                for (var attempt = 0; ; attempt++)
                {
                    try { Directory.Delete(tempRoot, recursive: true); break; }
                    catch (IOException) when (attempt < 10) { await Task.Delay(100 * (attempt + 1)); }
                }
            }
        }
    }

    private static async Task<string> CreateRestrictedApiConnectionAsync(IntegrationDatabase database)
    {
        var login = $"api_e2e_{Guid.NewGuid():N}";
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var command = new NpgsqlCommand(
            $"CREATE ROLE \"{login}\" LOGIN INHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS PASSWORD '{password}'", connection))
            await command.ExecuteNonQueryAsync();
        await using (var command = new NpgsqlCommand($"GRANT nexoruta_api TO \"{login}\"", connection))
            await command.ExecuteNonQueryAsync();
        var builder = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Username = login,
            Password = password,
            Pooling = false
        };
        return builder.ConnectionString;
    }

    private static async Task<(int Shipments, int Outbox)> CountsAsync(IntegrationDatabase database)
    {
        await using var db = database.CreateDbContext();
        return (await db.Envios.CountAsync(), await db.OutboxMessages.CountAsync());
    }

    private static async Task<int> CountOutboxAsync(IntegrationDatabase database)
    {
        await using var db = database.CreateDbContext();
        return await db.OutboxMessages.CountAsync();
    }

    private static async Task<string> LoginAsync(HttpClient client, string email, string password)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { Email = email, Password = password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("accessToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> PostCsvAsync(HttpClient client, Guid operatorId, string csv)
        => await PostCsvBytesAsync(client, operatorId, Encoding.UTF8.GetBytes(csv));

    private static async Task<HttpResponseMessage> PostCsvBytesAsync(HttpClient client, Guid operatorId, byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv") { CharSet = "utf-8" };
        form.Add(file, "archivo", "envios.csv");
        form.Add(new StringContent(operatorId.ToString("D"), Encoding.UTF8), "operadorId");
        return await client.PostAsync("/api/envios/importaciones", form);
    }

    private static string FirstState(string json) => States(json)[0];
    private static string[] States(string json)
    {
        using var body = JsonDocument.Parse(json);
        return body.RootElement.GetProperty("filas").EnumerateArray()
            .Select(row => row.GetProperty("estado").GetString()!).ToArray();
    }

    private static byte[] CreateBoundaryCsv(long targetBytes)
    {
        var validCsv = Encoding.UTF8.GetBytes(Csv(Row("KESTREL-EXACT-LIMIT")));
        var result = new byte[checked((int)targetBytes)];
        Array.Copy(validCsv, result, validCsv.Length);
        Array.Fill(result, (byte)'\n', validCsv.Length, result.Length - validCsv.Length);
        return result;
    }

    private static string Csv(params string[] rows) => Header + "\r\n" + string.Join("\r\n", rows);
    private static string Row(string reference, string name = "Ada Lovelace", string weight = "500")
        => $"{reference},{name},ada@example.test,\"18 de Julio 123\",BOX-1,{weight},20,15,10";

    private static string FindWorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend", "src", "NexoRuta.Commerce", "NexoRuta.Commerce.csproj")))
                return directory.FullName;
        throw new DirectoryNotFoundException("The NexoRuta workspace root could not be found.");
    }

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string FindChromium()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ms-playwright");
        if (!Directory.Exists(root)) return string.Empty;
        return Directory.GetDirectories(root, "chromium-*")
            .Select(path => Path.Combine(path, "chrome-win64", "chrome.exe"))
            .Where(File.Exists)
            .OrderByDescending(path => path, StringComparer.Ordinal)
            .FirstOrDefault() ?? string.Empty;
    }

    private static KestrelProcess StartDotnet(string dll, string workingDirectory, string url,
        IReadOnlyDictionary<string, string?> environment)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(dll);
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add(url);
        foreach (var (name, value) in environment)
            start.Environment[name] = value;
        var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start an isolated Kestrel process.");
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        return new(process);
    }

    private static async Task WaitForHttpAsync(Process process, string url)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process.HasExited)
                throw new InvalidOperationException($"Kestrel process exited before readiness (exit code {process.ExitCode}).");
            try
            {
                using var response = await client.GetAsync(url);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(250);
        }
        throw new TimeoutException("Kestrel did not become ready within 90 seconds.");
    }

    private sealed class KestrelProcess(Process process) : IAsyncDisposable
    {
        public Process Process { get; } = process;
        public async ValueTask DisposeAsync()
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
                await Process.WaitForExitAsync();
            }
            Process.Dispose();
        }
    }

    private sealed class ChromiumPage : IAsyncDisposable
    {
        private readonly ClientWebSocket socket = new();
        private readonly HttpClient devTools = new();
        private readonly Process process;
        private int commandId;

        private ChromiumPage(Process process) => this.process = process;

        public static async Task<ChromiumPage> StartAsync(string executable, string profile, string initialUrl)
        {
            var port = FindFreePort();
            Directory.CreateDirectory(profile);
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            foreach (var argument in new[] { "--headless=new", "--disable-gpu", "--no-sandbox", "--disable-dev-shm-usage",
                         $"--remote-debugging-port={port}", $"--user-data-dir={profile}", "--no-first-run", "--no-default-browser-check" })
                start.ArgumentList.Add(argument);
            var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start the cached Chromium browser.");
            _ = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();
            var page = new ChromiumPage(process);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            JsonDocument? target = null;
            while (DateTimeOffset.UtcNow < deadline && target is null)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Put, $"http://127.0.0.1:{port}/json/new?{initialUrl}");
                    using var response = await page.devTools.SendAsync(request);
                    if (response.IsSuccessStatusCode)
                        target = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                }
                catch (HttpRequestException) { }
                await Task.Delay(150);
            }
            if (target is null)
            {
                await page.DisposeAsync();
                throw new TimeoutException("Cached Chromium did not expose its DevTools endpoint.");
            }
            await page.socket.ConnectAsync(new Uri(target.RootElement.GetProperty("webSocketDebuggerUrl").GetString()!), CancellationToken.None);
            target.Dispose();
            await page.CallAsync("Page.enable", new { });
            await page.CallAsync("Runtime.enable", new { });
            await page.CallAsync("DOM.enable", new { });
            await page.CallAsync("CSS.enable", new { });
            await page.WaitForAsync("document.readyState === 'complete'");
            return page;
        }

        public async Task NavigateAsync(string url)
        {
            await CallAsync("Page.navigate", new { url });
            await WaitForAsync("document.readyState === 'complete'");
            await Task.Delay(300);
        }

        public async Task LoginAsync(string email, string password, string returnPath = "/envios")
        {
            await WaitForAsync("document.querySelector('input[name=Email]') !== null");
            var emailLiteral = JsonSerializer.Serialize(email);
            var passwordLiteral = JsonSerializer.Serialize(password);
            await EvaluateAsync($"document.querySelector('input[name=Email]').value={emailLiteral}; document.querySelector('input[name=Password]').value={passwordLiteral}; document.querySelector('form').requestSubmit(); true");
            var returnPathLiteral = JsonSerializer.Serialize(returnPath);
            await WaitForAsync($"location.pathname === {returnPathLiteral} || location.pathname === {returnPathLiteral} + '/'");
        }

        public async Task SelectFileAndOperatorAsync(string path, Guid operatorId)
        {
            await EvaluateAsync($"(()=>{{const s=document.querySelector('#import-operator');if(!s)return false;s.value={JsonSerializer.Serialize(operatorId.ToString("D"))};s.dispatchEvent(new Event('change',{{bubbles:true}}));return s.value!=='';}})()");
            var root = await CallAsync("DOM.getDocument", new { depth = -1, pierce = true });
            var nodeId = root.GetProperty("result").GetProperty("root").GetProperty("nodeId").GetInt32();
            var input = await CallAsync("DOM.querySelector", new { nodeId, selector = "#import-file" });
            var inputNodeId = input.GetProperty("result").GetProperty("nodeId").GetInt32();
            Assert.NotEqual(0, inputNodeId);
            await CallAsync("DOM.setFileInputFiles", new { files = new[] { path }, nodeId = inputNodeId });
            await WaitForAsync("document.querySelector('.selected-file') !== null && !document.querySelector('button.btn-primary').disabled");
        }

        public async Task CreateIndividualShipmentAsync(Guid operatorId)
        {
            await EvaluateAsync($"(()=>{{const s=document.querySelector('#envio-operador');s.value={JsonSerializer.Serialize(operatorId.ToString("D"))};s.dispatchEvent(new Event('change',{{bubbles:true}}));return true;}})()");
            await WaitForAsync("document.querySelector('form.nr-creation-form button[type=submit]') && !document.querySelector('form.nr-creation-form button[type=submit]').disabled");
            await EvaluateAsync("(()=>{const f=document.querySelector('form.nr-creation-form');const labels=Array.from(f.querySelectorAll('label'));const find=t=>labels.find(l=>l.textContent.trim().startsWith(t))?.querySelector('input');const fields=[[find('Nombre'),'Commerce browser individual'],[find('Correo'),'commerce-individual@example.test'],[find('Dirección'),'18 de Julio 123'],[find('Código'),'BROWSER-INDIVIDUAL'],[find('Peso'),'500'],[find('Largo'),'20'],[find('Ancho'),'15'],[find('Alto'),'10']];if(fields.some(([i])=>!i))throw new Error('Individual form fields were not found by their labels.');for(const [i,v] of fields){i.value=v;i.dispatchEvent(new Event('input',{bubbles:true}));i.dispatchEvent(new Event('change',{bubbles:true}));}f.querySelector('button[type=submit]').click();return true;})()");
            await WaitForAsync("document.querySelector('.alert-success')?.innerText.toLowerCase().includes('guardado') === true");
        }

        public async Task FillReturnAndSetStaleVersionAsync(string reason)
        {
            var literal = JsonSerializer.Serialize(reason);
            var result = await EvaluateAsync($"(()=>{{const f=document.querySelector('form[action*=\\\"IniciarDevolucion\\\"]');if(!f)return false;const m=f.querySelector('textarea[name=MotivoDevolucion]');const c=f.querySelector('input[name=ConfirmarDevolucion]');const v=f.querySelector('input[name=VersionEnvio]');if(!m||!c||!v)return false;m.value={literal};m.dispatchEvent(new Event('input',{{bubbles:true}}));c.checked=true;v.value='0';return true;}})()");
            Assert.Equal(JsonValueKind.True, result.ValueKind);
        }

        public async Task FillLossAndSetStaleVersionAsync(string reason)
        {
            var literal = JsonSerializer.Serialize(reason);
            var result = await EvaluateAsync($"(()=>{{const f=document.querySelector('form[action*=\\\"ConfirmarExtravio\\\"]');if(!f)return false;const m=f.querySelector('textarea[name=MotivoExtravio]');const c=f.querySelector('input[name=ConfirmarExtravio]');const v=f.querySelector('input[name=VersionEnvio]');if(!m||!c||!v)return false;m.value={literal};m.dispatchEvent(new Event('input',{{bubbles:true}}));c.checked=true;v.value='0';return true;}})()");
            Assert.Equal(JsonValueKind.True, result.ValueKind);
        }

        public async Task FillLossAsync(string reason)
        {
            var literal = JsonSerializer.Serialize(reason);
            var result = await EvaluateAsync($"(()=>{{const f=document.querySelector('form[action*=\\\"ConfirmarExtravio\\\"]');if(!f)return false;const m=f.querySelector('textarea[name=MotivoExtravio]');const c=f.querySelector('input[name=ConfirmarExtravio]');if(!m||!c)return false;m.value={literal};m.dispatchEvent(new Event('input',{{bubbles:true}}));c.checked=true;return true;}})()");
            Assert.Equal(JsonValueKind.True, result.ValueKind);
        }

        public async Task ClickImportAndWaitAsync(string rowState, bool doubleClick = false)
        {
            await EvaluateAsync(doubleClick
                ? "(()=>{const b=document.querySelector('button.btn-primary');b.click();b.click();return true;})()"
                : "document.querySelector('button.btn-primary').click(); true");
            await WaitForAsync($"document.querySelector('.import-result-list')?.innerText.includes({JsonSerializer.Serialize(rowState)}) === true");
            await WaitForAsync("document.querySelector('.import-result-list')?.innerText.includes('Ya existente') === true || document.querySelector('.import-result-list')?.innerText.includes('Creado') === true");
        }

        public async Task ClickImportAndWaitForRejectionAsync()
        {
            await EvaluateAsync("document.querySelector('button.btn-primary').click(); true");
            await WaitForAsync("document.querySelector('.import-result')?.innerText.includes('archivo fue rechazado') === true");
            var globalSuccess = await EvaluateBooleanAsync("document.querySelector('.import-result .alert-success') !== null");
            Assert.False(globalSuccess);
        }

        public async Task SetViewportAsync(int width, int height, bool mobile)
        {
            await CallAsync("Emulation.setDeviceMetricsOverride", new { width, height, deviceScaleFactor = 1, mobile });
            await Task.Delay(200);
        }

        public async Task<bool> EvaluateBooleanAsync(string expression)
        {
            var value = await EvaluateAsync(expression);
            return value.ValueKind == JsonValueKind.True;
        }

        public async Task<string> EvaluateStringAsync(string expression)
        {
            var value = await EvaluateAsync(expression);
            return value.GetString() ?? string.Empty;
        }

        public async Task<string> GetPlatformFontsAsync(string selector)
        {
            var document = await CallAsync("DOM.getDocument", new { depth = 0, pierce = true });
            var rootNodeId = document.GetProperty("result").GetProperty("root").GetProperty("nodeId").GetInt32();
            var query = await CallAsync("DOM.querySelector", new { nodeId = rootNodeId, selector });
            var nodeId = query.GetProperty("result").GetProperty("nodeId").GetInt32();
            Assert.NotEqual(0, nodeId);
            var response = await CallAsync("CSS.getPlatformFontsForNode", new { nodeId });
            return string.Join(", ", response.GetProperty("result").GetProperty("fonts").EnumerateArray()
                .Select(font => $"{font.GetProperty("familyName").GetString()} ({font.GetProperty("glyphCount").GetInt32()} glyphs)"));
        }

        public async Task<bool> HasSingleSharedBackofficeShellAsync()
            => await EvaluateBooleanAsync("(()=>{const shell=document.querySelector('#backoffice-app-shell');return Boolean(shell&&shell.querySelectorAll(':scope > .nr-topbar').length===1&&shell.querySelectorAll(':scope > .nr-portal-layout').length===1&&shell.querySelectorAll('#backofficeNavigation.nr-sidebar').length===1&&shell.querySelectorAll('#main-content').length===1&&document.querySelectorAll('.nr-topbar').length===1&&document.querySelectorAll('#backofficeNavigation').length===1&&document.querySelectorAll('#main-content').length===1&&document.querySelectorAll('.nr-routes-app,.nr-routes-sidebar,.nr-routes-topbar').length===0);})()");

        public async Task<string> GetSharedShellSignatureAsync()
        {
            const string expression = """
                JSON.stringify((()=>{
                  const shell=document.querySelector('#backoffice-app-shell');
                  const topbar=shell?.querySelector(':scope > .nr-topbar');
                  const layout=shell?.querySelector(':scope > .nr-portal-layout');
                  const sidebar=shell?.querySelector('#backofficeNavigation.nr-sidebar');
                  const navigation=sidebar?.querySelector('.nr-navigation');
                  const main=shell?.querySelector('#main-content.nr-main-content');
                  const brand=topbar?.querySelector('.nr-brand');
                  const styles=(element,names)=>element?Object.fromEntries(names.map(name=>[name,getComputedStyle(element)[name]])):null;
                  if(!shell||!topbar||!layout||!sidebar||!navigation||!main||!brand)return JSON.stringify({missing:true});
                  const links=[...navigation.querySelectorAll('a.nr-nav-link')].map(link=>({
                    text:link.innerText.trim(),href:link.getAttribute('href'),
                    classes:[...link.classList].filter(value=>value!=='active').sort(),
                    style:styles(link,['display','fontFamily','fontSize','lineHeight','paddingBlockStart','paddingInlineStart','borderRadius','minHeight'])
                  }));
                  const groups=[...navigation.querySelectorAll(':scope > .nr-nav-group')].map(group=>({
                    heading:group.querySelector('.nr-nav-heading')?.innerText.trim()||'',
                    links:[...group.querySelectorAll('a.nr-nav-link')].map(link=>link.getAttribute('href'))
                  }));
                  return JSON.stringify({
                    shellClass:shell.className,
                    topbar:{className:topbar.className,text:brand.innerText.trim(),style:styles(topbar,['display','alignItems','backgroundColor','borderBottomColor','borderBottomWidth','color','fontFamily','fontSize','fontWeight','height','paddingBlockStart','paddingInlineStart','paddingInlineEnd']),brandStyle:styles(brand,['display','alignItems','color','fontFamily','fontSize','fontWeight','lineHeight','textDecorationLine']),
                      actionsText:topbar.querySelector('.nr-topbar-actions')?.innerText.trim(),logoutForms:topbar.querySelectorAll('form').length,toggles:topbar.querySelectorAll('.nr-menu-toggle').length},
                    layout:{className:layout.className,display:styles(layout,['display','gap','alignItems'])},
                    sidebar:{className:[...sidebar.classList].filter(value=>value!=='show').sort(),style:styles(sidebar,['display','position','top','minHeight','paddingBlockStart','paddingInlineStart','paddingInlineEnd','backgroundColor','borderInlineEndColor','borderInlineEndWidth'])},
                    navigation:{className:navigation.className,label:navigation.getAttribute('aria-label'),groups,links},
                    main:{className:main.className,role:main.getAttribute('role'),tabindex:main.getAttribute('tabindex'),style:styles(main,['display','paddingBlockStart','paddingInlineStart','paddingInlineEnd','fontFamily','color'])},
                    footer:shell.querySelector('.nr-footer')?.innerText.trim()||''
                  });
                })())
                """;
            return await EvaluateStringAsync(expression);
        }

        public async Task ToggleBackofficeNavigationWithKeyboardAsync(bool open)
        {
            var focused = await EvaluateAsync("(()=>{const button=document.querySelector('.nr-topbar .nr-menu-toggle');if(!button)return false;button.focus();return document.activeElement===button;})()");
            Assert.Equal(JsonValueKind.True, focused.ValueKind);
            await CallAsync("Input.dispatchKeyEvent", new { type = "keyDown", key = "Enter", code = "Enter", text = "\r", unmodifiedText = "\r", windowsVirtualKeyCode = 13 });
            await CallAsync("Input.dispatchKeyEvent", new { type = "keyUp", key = "Enter", code = "Enter", windowsVirtualKeyCode = 13 });
            await WaitForAsync($"document.querySelector('#backofficeNavigation')?.classList.contains('show')==={(open ? "true" : "false")}");
            Assert.True(await EvaluateBooleanAsync($"document.querySelector('.nr-topbar .nr-menu-toggle')?.getAttribute('aria-expanded')==='{open.ToString().ToLowerInvariant()}'&&document.activeElement===document.querySelector('.nr-topbar .nr-menu-toggle')"),
                "The shared mobile navigation toggle should preserve focus and reflect its expanded state.");
        }

        public async Task ClickButtonContainingAsync(string text)
        {
            var literal = JsonSerializer.Serialize(text);
            await EvaluateAsync($"(()=>{{const b=Array.from(document.querySelectorAll('button')).find(button=>button.innerText.includes({literal}));if(!b)return false;b.click();return true;}})()");
        }

        public async Task ClickButtonContainingKeyboardAsync(string text)
        {
            var literal = JsonSerializer.Serialize(text);
            var focused = await EvaluateAsync($"(()=>{{const b=Array.from(document.querySelectorAll('button')).find(button=>button.innerText.includes({literal}));if(!b)return false;b.focus();return document.activeElement===b;}})()");
            Assert.Equal(JsonValueKind.True, focused.ValueKind);
            await CallAsync("Input.dispatchKeyEvent", new { type = "keyDown", key = "Enter", code = "Enter", text = "\r", unmodifiedText = "\r", windowsVirtualKeyCode = 13 });
            await CallAsync("Input.dispatchKeyEvent", new { type = "keyUp", key = "Enter", code = "Enter", windowsVirtualKeyCode = 13 });
        }

        public async Task ClickLinkContainingKeyboardAsync(string text)
        {
            var literal = JsonSerializer.Serialize(text);
            var focused = await EvaluateAsync($"(()=>{{const a=Array.from(document.querySelectorAll('a')).find(link=>link.innerText.includes({literal}));if(!a)return false;a.focus();return document.activeElement===a;}})()");
            Assert.Equal(JsonValueKind.True, focused.ValueKind);
            await CallAsync("Input.dispatchKeyEvent", new { type = "keyDown", key = "Enter", code = "Enter", text = "\r", unmodifiedText = "\r", windowsVirtualKeyCode = 13 });
            await CallAsync("Input.dispatchKeyEvent", new { type = "keyUp", key = "Enter", code = "Enter", windowsVirtualKeyCode = 13 });
        }

        public async Task ClickReceiptForShipmentKeyboardAsync(Guid shipmentId)
        {
            var literal = JsonSerializer.Serialize(shipmentId.ToString("D"));
            var focused = await EvaluateAsync($"(()=>{{const input=Array.from(document.querySelectorAll('.nr-receipt-card input[name=envioId]')).find(x=>x.value==={literal});const button=input?.closest('form')?.querySelector('button[type=submit]');if(!button)return false;button.focus();return document.activeElement===button;}})()");
            Assert.Equal(JsonValueKind.True, focused.ValueKind);
            await CallAsync("Input.dispatchKeyEvent", new { type = "keyDown", key = "Enter", code = "Enter", text = "\r", unmodifiedText = "\r", windowsVirtualKeyCode = 13 });
            await CallAsync("Input.dispatchKeyEvent", new { type = "keyUp", key = "Enter", code = "Enter", windowsVirtualKeyCode = 13 });
        }

        public async Task CaptureScreenshotAsync(string path, bool fullPage = false)
        {
            var result = await CallAsync("Page.captureScreenshot", new { format = "png", captureBeyondViewport = fullPage });
            await File.WriteAllBytesAsync(path, Convert.FromBase64String(result.GetProperty("result").GetProperty("data").GetString()!));
        }

        public async Task<bool> ToggleFirstRouteActionWithSpaceAsync()
        {
            var focused = await EvaluateAsync("(()=>{const summary=document.querySelector('.nr-route-detail-panel .nr-context-action > summary');if(!summary)return false;summary.scrollIntoView({block:'center'});summary.focus();return document.activeElement===summary;})()");
            if (focused.ValueKind != JsonValueKind.True) return false;
            await CallAsync("Input.dispatchKeyEvent", new { type = "keyDown", key = " ", code = "Space", text = " ", unmodifiedText = " ", windowsVirtualKeyCode = 32 });
            await CallAsync("Input.dispatchKeyEvent", new { type = "keyUp", key = " ", code = "Space", windowsVirtualKeyCode = 32 });
            return await EvaluateBooleanAsync("document.querySelector('.nr-route-detail-panel .nr-context-action')?.open === true");
        }

        public async Task ScrollResultsIntoViewAsync()
            => await EvaluateAsync("document.querySelector('.import-result')?.scrollIntoView({block:'start'}); true");

        public async Task VerifyKeyboardFocusAsync()
        {
            await EvaluateAsync("document.body.focus(); true");
            await CallAsync("Input.dispatchKeyEvent", new { type = "keyDown", key = "Tab", code = "Tab", windowsVirtualKeyCode = 9 });
            await CallAsync("Input.dispatchKeyEvent", new { type = "keyUp", key = "Tab", code = "Tab", windowsVirtualKeyCode = 9 });
            var value = await EvaluateAsync("document.activeElement && document.activeElement.tagName !== 'BODY'");
            Assert.Equal(JsonValueKind.True, value.ValueKind);
        }

        public async Task WaitForAsync(string expression)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    if (await EvaluateBooleanAsync(expression)) return;
                }
                catch (Exception) when (!process.HasExited) { }
                await Task.Delay(200);
            }
            throw new TimeoutException("Browser condition was not reached.");
        }

        private async Task<JsonElement> EvaluateAsync(string expression)
        {
            var response = await CallAsync("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = true });
            var evaluation = response.GetProperty("result");
            if (evaluation.TryGetProperty("exceptionDetails", out var details))
            {
                var description = details.TryGetProperty("exception", out var exception) && exception.TryGetProperty("description", out var exceptionDescription)
                    ? exceptionDescription.GetString()
                    : details.GetProperty("text").GetString();
                throw new InvalidOperationException($"The browser evaluation returned a JavaScript error: {description}");
            }
            var remote = evaluation.GetProperty("result");
            if (remote.TryGetProperty("value", out var value)) return value.Clone();
            if (remote.TryGetProperty("subtype", out var subtype) && subtype.GetString() == "error")
                throw new InvalidOperationException("The browser evaluation returned a JavaScript error.");
            return default;
        }

        private async Task<JsonElement> CallAsync(string method, object parameters)
        {
            var id = Interlocked.Increment(ref commandId);
            var request = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });
            await socket.SendAsync(request, WebSocketMessageType.Text, true, CancellationToken.None);
            while (socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult part;
                do
                {
                    var buffer = new byte[16 * 1024];
                    part = await socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (part.MessageType == WebSocketMessageType.Close)
                        throw new WebSocketException("Chromium closed its DevTools connection.");
                    message.Write(buffer, 0, part.Count);
                } while (!part.EndOfMessage);
                using var parsed = JsonDocument.Parse(message.ToArray());
                var root = parsed.RootElement;
                if (!root.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id)
                    continue;
                if (root.TryGetProperty("error", out _))
                    throw new InvalidOperationException($"Chromium rejected DevTools command {method}: {root.GetProperty("error").GetRawText()}");
                return root.Clone();
            }
            throw new WebSocketException("Chromium DevTools connection is not open.");
        }

        public async ValueTask DisposeAsync()
        {
            socket.Dispose();
            devTools.Dispose();
            if (!process.HasExited)
            {
                if (OperatingSystem.IsWindows())
                {
                    using var treeKill = Process.Start(new ProcessStartInfo("taskkill.exe")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        ArgumentList = { "/PID", process.Id.ToString(CultureInfo.InvariantCulture), "/T", "/F" }
                    });
                    if (treeKill is not null) await treeKill.WaitForExitAsync();
                }
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            process.Dispose();
        }
    }
}
