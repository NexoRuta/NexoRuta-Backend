using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NexoRuta.IntegrationTests.Fixtures;
using Npgsql;

namespace NexoRuta.IntegrationTests.Seguridad;

public sealed class PortalCrossNodeTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task AntiforgeryAndSessionCrossBackofficeHostsAndSurviveRestartButNotAdmin()
    {
        var database = await postgres.CreateDatabaseAsync();
        using (var seed = database.CreateApiFactory()) using (seed.CreateHttpClient()) { }
        string email;
        await using (var db = database.CreateDbContext())
        {
            var access = await db.AccesosUsuario.SingleAsync(x => x.OperadorId != null);
            email = (await db.Usuarios.SingleAsync(x => x.Id == access.UsuarioId)).Email!;
        }
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(database);
        using var api = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings(), seedOperationalRules: false);
        api.UseKestrel(0);
        using var apiClient = api.CreateClient();
        await using var backofficeKeys = await SharedDataProtectionTestStore.CreateAsync(database, "backoffice");
        await using var adminKeys = await SharedDataProtectionTestStore.CreateAsync(database, "admin");
        var cookies = new CookieContainer();
        using var handler = new HttpClientHandler
        {
            CookieContainer = cookies, AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        using var rawHandler = new HttpClientHandler
        {
            UseCookies = false, AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var rawClient = new HttpClient(rawHandler) { Timeout = TimeSpan.FromSeconds(15) };
        var a = FreePort(); var b = FreePort(); var adminPort = FreePort();
        var uriA = new Uri($"https://127.0.0.1:{a}/");
        var uriB = new Uri($"https://127.0.0.1:{b}/");
        var adminUri = new Uri($"https://127.0.0.1:{adminPort}/");
        Process? portalA = null, portalB = null, admin = null;
        try
        {
            portalA = await StartPortal("Backoffice", a, apiClient.BaseAddress!, backofficeKeys, client);
            portalB = await StartPortal("Backoffice", b, apiClient.BaseAddress!, backofficeKeys, client);
            admin = await StartPortal("Admin", adminPort, apiClient.BaseAddress!, adminKeys, client);
            using var loginPage = await client.GetAsync(new Uri(uriA, "ingresar"));
            Assert.Equal(HttpStatusCode.OK, loginPage.StatusCode);
            var html = await loginPage.Content.ReadAsStringAsync();
            var match = Regex.Match(html, "<input[^>]*name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
            Assert.True(match.Success, "The login page must emit its antiforgery token.");
            var token = WebUtility.HtmlDecode(match.Groups[1].Value);
            var antiCookie = cookies.GetCookies(uriA)["NexoRuta.Backoffice.Antiforgery"];
            Assert.NotNull(antiCookie);
            using var login = await client.PostAsync(new Uri(uriB, "ingresar"), new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Email"] = email, ["Password"] = AuthenticationTestData.Password,
                ["ReturnUrl"] = "/", ["__RequestVerificationToken"] = token
            }));
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            var ticket = cookies.GetCookies(uriB)["NexoRuta.Backoffice"];
            Assert.NotNull(ticket);
            Assert.True(ticket!.Secure);
            using (var home = await client.GetAsync(uriA)) Assert.Equal(HttpStatusCode.OK, home.StatusCode);

            using var crossCookie = new HttpRequestMessage(HttpMethod.Get, adminUri);
            crossCookie.Headers.Add("Cookie", $"NexoRuta.Admin={ticket.Value}");
            using var denied = await rawClient.SendAsync(crossCookie);
            Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
            Assert.Contains("ingresar", denied.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
            using var crossToken = new HttpRequestMessage(HttpMethod.Post, new Uri(adminUri, "ingresar"));
            crossToken.Headers.Add("Cookie", $"NexoRuta.Admin.Antiforgery={antiCookie!.Value}");
            crossToken.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Email"] = email, ["Password"] = AuthenticationTestData.Password,
                ["__RequestVerificationToken"] = token
            });
            using var rejectedToken = await rawClient.SendAsync(crossToken);
            Assert.Equal(HttpStatusCode.BadRequest, rejectedToken.StatusCode);

            var keyCount = await KeyCount(database);
            Assert.True(keyCount > 0);
            await StopPortal(portalB); portalB = null;
            portalB = await StartPortal("Backoffice", b, apiClient.BaseAddress!, backofficeKeys, client);
            using (var reopened = await client.GetAsync(uriB)) Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
            Assert.Equal(keyCount, await KeyCount(database));
        }
        finally
        {
            if (admin is not null) await StopPortal(admin);
            if (portalB is not null) await StopPortal(portalB);
            if (portalA is not null) await StopPortal(portalA);
        }
    }

    private static async Task<long> KeyCount(IntegrationDatabase database)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM nexoruta_keys_backoffice.\"DataProtectionKeys\"", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static async Task<Process> StartPortal(string portal, int port, Uri api,
        SharedDataProtectionTestStore keys, HttpClient client)
    {
        var root = Environment.GetEnvironmentVariable("NEXORUTA_SESSION_PORTAL_ROOT")
            ?? Path.Combine(CompanionPortals.WorkspaceRoot, "frontend");
        var project = "NexoRuta." + portal;
        var directory = Path.Combine(root, "src", project);
        var dll = Path.Combine(directory, "bin", CompanionPortals.Configuration, "net10.0", project + ".dll");
        Assert.True(File.Exists(dll), "Build the selected frontend companion before running this test.");
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        var dotnet = string.IsNullOrEmpty(dotnetRoot) ? "dotnet" : Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var start = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(dll);
        start.Environment["ASPNETCORE_URLS"] = $"https://127.0.0.1:{port}";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["ASPNETCORE_Kestrel__Certificates__Default__Path"] = keys.CertificatePath;
        start.Environment["Api__BaseAddress"] = api.ToString();
        start.Environment["ConnectionStrings__DataProtection"] = keys.ConnectionString;
        start.Environment["DataProtection__CertificatePath"] = keys.CertificatePath;
        start.Environment["OpenTelemetry__Enabled"] = "false";
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the synthetic portal.");
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        try
        {
            var ready = new Uri($"https://127.0.0.1:{port}/ready");
            for (var i = 0; i < 150; i++)
            {
                try { using var response = await client.GetAsync(ready); if (response.IsSuccessStatusCode) return process; }
                catch (HttpRequestException) { }
                if (process.HasExited) break;
                await Task.Delay(100);
            }
            throw new InvalidOperationException($"The synthetic {portal} portal did not become ready.");
        }
        catch { await StopPortal(process); throw; }
    }

    private static async Task StopPortal(Process process)
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        process.Dispose();
    }
}
