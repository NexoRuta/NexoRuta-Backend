using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Npgsql;

namespace NexoRuta.IntegrationTests.Fixtures;

// Opt-in Linux fixture: creates its own cluster, never accepts an external connection.
internal sealed class NativePostgres : IAsyncDisposable
{
    private const string Bin = "/usr/lib/postgresql/18/bin/";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "nexoruta-owned-pg-" + Guid.NewGuid().ToString("N"));
    private readonly bool root = Environment.UserName == "root";
    private bool started;
    public string ConnectionString { get; private set; } = "";

    public async Task StartAsync(string database, string username, string password)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(Bin + "initdb"))
            throw new InvalidOperationException("Native fixture requires Linux PostgreSQL18/PostGIS; run scripts/cloud/install.sh.");
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var passfile = Path.Combine(directory, "initial-password");
        await File.WriteAllTextAsync(passfile, password);
        File.SetUnixFileMode(passfile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        if (root)
        {
            await ExecuteAsync("chown", ["postgres:postgres", directory, passfile]);
        }
        try
        {
            await PgAsync("initdb", ["-D", Path.Combine(directory, "data"), "--username", username,
                "--pwfile", passfile, "--auth-host=scram-sha-256", "--auth-local=trust"]);
        }
        finally { File.Delete(passfile); }
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        // A conflicting port fails startup; never attach to another cluster after a bind race.
        await PgAsync("pg_ctl", ["-D", Path.Combine(directory, "data"), "-l", Path.Combine(directory, "server.log"),
            "-o", $"-h 127.0.0.1 -p {port} -k {directory}", "-w", "start"]);
        started = true;
        var connection = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1", Port = port, Database = "postgres", Username = username,
            Password = password, Pooling = false
        };
        await using var admin = new NpgsqlConnection(connection.ConnectionString);
        await admin.OpenAsync();
        // Names are fixture-generated, not user input; quote identifiers nevertheless.
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database.Replace("\"", "\"\"")}\"", admin);
        await create.ExecuteNonQueryAsync();
        connection.Database = database;
        ConnectionString = connection.ConnectionString;
    }

    private Task PgAsync(string command, string[] arguments) => root
        ? ExecuteAsync("runuser", ["-u", "postgres", "--", Bin + command, .. arguments])
        : ExecuteAsync(Bin + command, arguments);

    private static async Task ExecuteAsync(string command, string[] arguments)
    {
        var start = new ProcessStartInfo(command)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw; }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Owned PostgreSQL command {command} failed: {await error}");
        await output;
    }

    public async ValueTask DisposeAsync()
    {
        if (started)
        {
            await PgAsync("pg_ctl", ["-D", Path.Combine(directory, "data"), "-m", "immediate", "-w", "stop"]);
            started = false;
        }
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
