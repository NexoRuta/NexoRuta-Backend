using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Testcontainers.Redis;

namespace NexoRuta.IntegrationTests.Fixtures;

public sealed class RedisTestServer : IAsyncDisposable
{
    public const string ContainerImage = "redis:7.2.16-alpine3.21";
    private const string NativeVersion = "7.2.16";

    private readonly RedisContainer? container;
    private readonly bool native;
    private readonly string? nativeDirectory;
    private readonly string? nativeExecutable;
    private readonly int nativePort;
    private Process? nativeProcess;
    private bool running;

    private RedisTestServer(RedisContainer container)
    {
        this.container = container;
    }

    private RedisTestServer(string executable, int port, string directory)
    {
        native = true;
        nativeExecutable = executable;
        nativePort = port;
        nativeDirectory = directory;
    }

    public string ConnectionString => container?.GetConnectionString()
        ?? $"127.0.0.1:{nativePort},abortConnect=false,connectRetry=1,connectTimeout=1000";

    public static async Task<RedisTestServer> CreateAsync(CancellationToken cancellationToken = default)
    {
        var mode = Environment.GetEnvironmentVariable("NEXORUTA_TEST_REDIS_MODE") ?? "docker";
        if (mode == "docker")
        {
            var server = new RedisTestServer(new RedisBuilder(ContainerImage).Build());
            try
            {
                await server.StartCoreAsync(cancellationToken);
                return server;
            }
            catch
            {
                await server.DisposeAsync();
                throw;
            }
        }
        if (mode != "native")
            throw new InvalidOperationException("NEXORUTA_TEST_REDIS_MODE must be docker or native.");
        if (!OperatingSystem.IsLinux())
            throw new InvalidOperationException("The native Redis test server requires Linux.");

        var executable = Environment.GetEnvironmentVariable("NEXORUTA_TEST_REDIS_SERVER") ?? "redis-server";
        await VerifyNativeVersionAsync(executable, cancellationToken);
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var directory = Path.Combine(Path.GetTempPath(), "nexoruta-owned-redis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var nativeServer = new RedisTestServer(executable, port, directory);
        try
        {
            await nativeServer.StartCoreAsync(cancellationToken);
            return nativeServer;
        }
        catch
        {
            await nativeServer.DisposeAsync();
            throw;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
        => StartCoreAsync(cancellationToken);

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        if (running) return;
        if (container is not null)
        {
            await container.StartAsync(cancellationToken);
        }
        else if (native)
        {
            var start = new ProcessStartInfo(nativeExecutable!)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in new[]
            {
                "--bind", "127.0.0.1", "--port", nativePort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--protected-mode", "yes", "--daemonize", "no", "--save", "", "--appendonly", "no",
                "--maxmemory", "32mb", "--maxmemory-policy", "allkeys-lru", "--dir", nativeDirectory!,
                "--dbfilename", "dump.rdb", "--logfile", Path.Combine(nativeDirectory!, "redis.log")
            })
                start.ArgumentList.Add(argument);
            nativeProcess = Process.Start(start)
                ?? throw new InvalidOperationException("Could not start the owned Redis test process.");
            nativeProcess.OutputDataReceived += (_, _) => { };
            nativeProcess.ErrorDataReceived += (_, _) => { };
            nativeProcess.BeginOutputReadLine();
            nativeProcess.BeginErrorReadLine();
            try
            {
                await WaitUntilReadyAsync(nativeProcess, cancellationToken);
            }
            catch
            {
                var failedProcess = nativeProcess;
                nativeProcess = null;
                if (!failedProcess.HasExited) failedProcess.Kill(entireProcessTree: true);
                await failedProcess.WaitForExitAsync();
                failedProcess.Dispose();
                throw;
            }
        }
        else
        {
            throw new InvalidOperationException("Redis test server has no configured runtime.");
        }
        running = true;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!running) return;
        if (container is not null)
        {
            await container.StopAsync(cancellationToken);
        }
        else if (nativeProcess is { } process)
        {
            nativeProcess = null;
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken);
            process.Dispose();
        }
        running = false;
    }

    private static async Task VerifyNativeVersionAsync(string executable, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable, "--version")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not inspect the native Redis executable.");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0 || !output.Contains($"v={NativeVersion}", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Native Redis test mode requires Redis {NativeVersion}; executable reported {output.Trim()} {error.Trim()}");
    }

    private async Task WaitUntilReadyAsync(Process process, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        var command = Encoding.ASCII.GetBytes("PING\r\n");
        var expected = Encoding.ASCII.GetBytes("+PONG\r\n");
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
                throw new InvalidOperationException("The owned Redis test process exited before becoming ready.");
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, nativePort, cancellationToken);
                await using var stream = client.GetStream();
                await stream.WriteAsync(command, cancellationToken);
                var response = new byte[expected.Length];
                var count = await stream.ReadAsync(response, cancellationToken);
                if (count == expected.Length && response.AsSpan().SequenceEqual(expected)) return;
            }
            catch (SocketException) { }
            catch (IOException) { }
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
        throw new TimeoutException("The owned Redis test process did not become ready within 15 seconds.");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync();
            if (container is not null) await container.DisposeAsync();
        }
        finally
        {
            if (nativeDirectory is not null && Directory.Exists(nativeDirectory))
                Directory.Delete(nativeDirectory, recursive: true);
        }
    }
}
