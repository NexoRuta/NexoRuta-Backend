using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;
using Npgsql;

namespace NexoRuta.IntegrationTests.Hosting;

public sealed class LinuxProcessFactAttribute : FactAttribute
{
    public LinuxProcessFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "SIGTERM process tests require Linux.";
    }
}

public sealed class ApiShutdownProcessTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [LinuxProcessFact]
    public async Task SigtermAllowsBlockedRecoveryRequestToCommitItsWholeOutboxPair()
    {
        var database = await postgres.CreateDatabaseAsync();
        using (var seed = database.CreateApiFactory()) using (seed.CreateHttpClient()) { }
        string email;
        await using (var owner = database.CreateDbContext())
        {
            var access = await owner.AccesosUsuario.SingleAsync(a => a.ComercioId != null && a.EsPropietario);
            email = (await owner.Usuarios.SingleAsync(u => u.Id == access.UsuarioId)).Email!;
            await owner.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION public.nfr13_hold_recovery() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."Tipo" = 'PasswordRecoveryEmail' THEN
                        PERFORM pg_catalog.pg_advisory_xact_lock(1314018378, 700);
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER nfr13_hold_recovery BEFORE INSERT ON public."OutboxMessages"
                    FOR EACH ROW EXECUTE FUNCTION public.nfr13_hold_recovery();
                """);
        }
        var restricted = await RlsRestrictedHttpFlowTests.RestrictApiAsync(database);
        var applicationName = $"nfr13_sigterm_{Guid.NewGuid():N}";
        var runtimeConnection = new NpgsqlConnectionStringBuilder(restricted.ConnectionString) { ApplicationName = applicationName };
        var keys = Directory.CreateTempSubdirectory("nexoruta-sigterm-keys-");
        var port = FreePort();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(20) };
        Process? api = null;
        try
        {
            api = StartApi(runtimeConnection.ConnectionString, keys.FullName, port);
            await WaitForReady(api, client);
            await using var blocker = new NpgsqlConnection(database.ConnectionString);
            await blocker.OpenAsync();
            await using var transaction = await blocker.BeginTransactionAsync();
            await using (var acquire = new NpgsqlCommand("SELECT pg_catalog.pg_advisory_xact_lock(1314018378, 700)", blocker, transaction))
                await acquire.ExecuteScalarAsync();
            var request = client.PostAsJsonAsync("/api/auth/password-recovery/request", new { Email = email });
            await WaitForBlockedDatabaseSession(database, applicationName);
            var started = Stopwatch.GetTimestamp();
            using (var signal = Process.Start(new ProcessStartInfo("/bin/kill")
            {
                UseShellExecute = false, CreateNoWindow = true,
                ArgumentList = { "-TERM", api.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            }) ?? throw new InvalidOperationException("Could not send SIGTERM to the synthetic API."))
            {
                await signal.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, signal.ExitCode);
            }
            await Task.Delay(250);
            Assert.False(api.HasExited);
            await transaction.CommitAsync();
            using var response = await request;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await api.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
            Assert.Equal(0, api.ExitCode);
            Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(30));
            await using var verify = database.CreateDbContext();
            Assert.Equal(1, await verify.OutboxMessages.CountAsync(x => x.Tipo == "PasswordRecoveryEmail"));
            Assert.Equal(1, await verify.OutboxMessages.CountAsync(x => x.Tipo == "PasswordRecoveryRequested"));
        }
        finally
        {
            if (api is not null)
            {
                if (!api.HasExited) api.Kill(entireProcessTree: true);
                await api.WaitForExitAsync();
                api.Dispose();
            }
            keys.Delete(recursive: true);
        }
    }

    private static Process StartApi(string connection, string keysDirectory, int port)
    {
        var workspace = CompanionPortals.WorkspaceRoot;
        var directory = Path.Combine(workspace, "backend", "src", "NexoRuta.Api");
        var dll = Path.Combine(directory, "bin", CompanionPortals.Configuration, "net10.0", "NexoRuta.Api.dll");
        Assert.True(File.Exists(dll), "Build the selected backend before running the process shutdown test.");
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        var executable = string.IsNullOrEmpty(root) ? "dotnet" : Path.Combine(root, "dotnet");
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(dll);
        start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
        start.Environment["DOTNET_ENVIRONMENT"] = "Testing";
        start.Environment["ConnectionStrings__Postgres"] = connection;
        start.Environment["ConnectionStrings__DataProtection"] = "";
        start.Environment["ConnectionStrings__RateLimits"] = "";
        start.Environment["DataProtection__KeysDirectory"] = keysDirectory;
        start.Environment["DatabaseInitialization__ApplyMigrations"] = "false";
        start.Environment["DatabaseInitialization__SeedData"] = "false";
        start.Environment["OpenTelemetry__Enabled"] = "false";
        start.Environment["PasswordRecovery__ResetPageUrl"] = "https://commerce.example.test/restablecer-contrasena";
        start.Environment["PasswordRecovery__TokenLifespanSeconds"] = "900";
        start.Environment["Email__Smtp__Host"] = "mailpit.integration.test";
        start.Environment["Email__Smtp__Port"] = "1025";
        start.Environment["Email__Smtp__FromAddress"] = "noreply@example.test";
        start.Environment["Tracking__PublicBaseUrl"] = "https://tracking.example.test";
        foreach (var (key, value) in AuthenticationTestData.Settings())
            start.Environment[key.Replace(":", "__")] = value;
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the synthetic API process.");
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        return process;
    }

    private static async Task WaitForReady(Process api, HttpClient client)
    {
        for (var i = 0; i < 150; i++)
        {
            try { using var response = await client.GetAsync("/ready"); if (response.IsSuccessStatusCode) return; }
            catch (HttpRequestException) { }
            if (api.HasExited) break;
            await Task.Delay(100);
        }
        throw new InvalidOperationException("The synthetic API process did not become ready.");
    }

    private static async Task WaitForBlockedDatabaseSession(IntegrationDatabase database, string applicationName)
    {
        await using var sql = new NpgsqlConnection(database.ConnectionString);
        await sql.OpenAsync();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        do
        {
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_stat_activity WHERE application_name = @application
                    AND wait_event_type = 'Lock' AND cardinality(pg_catalog.pg_blocking_pids(pid)) > 0)
                """, sql);
            command.Parameters.AddWithValue("application", applicationName);
            if ((bool)(await command.ExecuteScalarAsync())!) return;
            await Task.Delay(50);
        } while (DateTimeOffset.UtcNow < deadline);
        throw new TimeoutException("The recovery request did not reach the controlled database lock.");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
