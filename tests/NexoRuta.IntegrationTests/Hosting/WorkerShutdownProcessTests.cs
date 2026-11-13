using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Infrastructure.Outbox;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguimiento;
using Npgsql;
using Testcontainers.RabbitMq;

namespace NexoRuta.IntegrationTests.Hosting;

public sealed class WorkerShutdownProcessTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [LinuxProcessFact]
    public async Task SigtermDuringRabbitPublicationLeavesRecoverableReservation()
    {
        var database = await postgres.CreateDatabaseAsync();
        Guid messageId, shipmentId;
        await using (var owner = database.CreateDbContext())
        {
            var shipment = await SeguimientoTestData.CreateEnvioAsync(owner);
            shipmentId = shipment.Id;
            var message = OutboxMessage.ForShipment(shipment.OperadorId, shipment.Id,
                OutboxMessageTypes.TrackingRequested, OutboxDestino.RabbitMq,
                JsonSerializer.Serialize(new { envioId = shipment.Id, operadorId = shipment.OperadorId }));
            messageId = message.Id;
            owner.OutboxMessages.Add(message);
            await owner.SaveChangesAsync();
            await owner.OutboxMessages.Where(x => x.Destino == OutboxDestino.RabbitMq && x.Id != messageId)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.ProximoIntentoEnUtc, DateTimeOffset.UtcNow.AddDays(1)));
        }
        var workerConnection = await RestrictedWorkerConnection(database);
        var keys = Directory.CreateTempSubdirectory("nexoruta-worker-shutdown-");
        var encryptionKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await using var rabbit = new RabbitMqBuilder("rabbitmq:4-management").Build();
        Process? first = null, second = null;
        var paused = false;
        try
        {
            await rabbit.StartAsync();
            var address = new Uri(rabbit.GetConnectionString());
            await rabbit.PauseAsync(); paused = true;
            first = StartWorker(workerConnection, keys.FullName, encryptionKey, address);
            var reserved = await WaitForState(database, messageId, OutboxEstado.Procesando, first);
            Assert.Equal(1, reserved.Intentos);
            Assert.Null(reserved.PublicadoEnUtc);
            var started = Stopwatch.GetTimestamp();
            await SendTerm(first);
            await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, first.ExitCode);
            Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(30));
            await using (var owner = database.CreateDbContext())
            {
                var pending = await owner.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == messageId);
                Assert.Equal(OutboxEstado.Procesando, pending.Estado);
                Assert.Null(pending.PublicadoEnUtc);
                Assert.NotNull(pending.ProcesadorId);
                Assert.True(pending.ReservaHastaUtc > DateTimeOffset.UtcNow);
                await owner.OutboxMessages.Where(x => x.Id == messageId).ExecuteUpdateAsync(update =>
                    update.SetProperty(x => x.ReservaHastaUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));
            }
            await rabbit.UnpauseAsync(); paused = false;
            second = StartWorker(workerConnection, keys.FullName, encryptionKey, address);
            var published = await WaitForState(database, messageId, OutboxEstado.Publicado, second);
            Assert.Equal(2, published.Intentos);
            Assert.Null(published.ProcesadorId);
            Assert.Null(published.ReservaHastaUtc);
            var until = DateTimeOffset.UtcNow.AddSeconds(45);
            await using (var owner = database.CreateDbContext())
            {
                do
                {
                    if (await owner.ProcessedMessages.CountAsync(x => x.MessageId == messageId) == 1) break;
                    if (second.HasExited) throw new InvalidOperationException("The synthetic worker stopped before processing the message.");
                    await Task.Delay(100);
                } while (DateTimeOffset.UtcNow < until);
                Assert.Equal(1, await owner.ProcessedMessages.CountAsync(x => x.MessageId == messageId));
                Assert.Equal(1, await owner.EntregasSeguimiento.CountAsync(x => x.EnvioId == shipmentId));
            }
            await SendTerm(second);
            await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, second.ExitCode);
        }
        finally
        {
            if (paused) await rabbit.UnpauseAsync();
            foreach (var process in new[] { first, second })
            {
                if (process is null) continue;
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                process.Dispose();
            }
            keys.Delete(recursive: true);
        }
    }

    private static async Task<OutboxMessage> WaitForState(IntegrationDatabase database, Guid id,
        OutboxEstado expected, Process worker)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(60);
        await using var db = database.CreateDbContext();
        do
        {
            var row = await db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == id);
            if (row.Estado == expected) return row;
            if (worker.HasExited) throw new InvalidOperationException("The synthetic worker stopped before reaching the expected outbox state.");
            await Task.Delay(100);
        } while (DateTimeOffset.UtcNow < until);
        throw new TimeoutException("The synthetic worker did not reach the expected outbox state.");
    }

    private static Process StartWorker(string connection, string keys, string encryptionKey, Uri rabbit)
    {
        var directory = Path.Combine(CompanionPortals.WorkspaceRoot, "backend", "src", "NexoRuta.Worker");
        var dll = Path.Combine(directory, "bin", CompanionPortals.Configuration, "net10.0", "NexoRuta.Worker.dll");
        Assert.True(File.Exists(dll), "Build the selected backend before running the worker shutdown test.");
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        var executable = string.IsNullOrEmpty(root) ? "dotnet" : Path.Combine(root, "dotnet");
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(dll);
        var credentials = rabbit.UserInfo.Split(':', 2);
        Assert.Equal(2, credentials.Length);
        start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["Messaging__Transport"] = "RabbitMq";
        start.Environment["ConnectionStrings__Postgres"] = connection;
        start.Environment["ConnectionStrings__DataProtection"] = "";
        start.Environment["DataProtection__KeysDirectory"] = keys;
        start.Environment["RabbitMq__Host"] = rabbit.Host;
        start.Environment["RabbitMq__Port"] = rabbit.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["RabbitMq__Username"] = Uri.UnescapeDataString(credentials[0]);
        start.Environment["RabbitMq__Password"] = Uri.UnescapeDataString(credentials[1]);
        start.Environment["Seguimiento__EncryptionKey"] = encryptionKey;
        start.Environment["Tracking__PublicBaseUrl"] = "https://tracking.example.test";
        start.Environment["Email__Smtp__Host"] = "mailpit.integration.test";
        start.Environment["Email__Smtp__Port"] = "1025";
        start.Environment["Email__Smtp__FromAddress"] = "noreply@example.test";
        start.Environment["OpenTelemetry__Enabled"] = "false";
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the synthetic worker.");
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        return process;
    }

    private static async Task SendTerm(Process worker)
    {
        using var signal = Process.Start(new ProcessStartInfo("/bin/kill")
        {
            UseShellExecute = false, CreateNoWindow = true,
            ArgumentList = { "-TERM", worker.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) }
        }) ?? throw new InvalidOperationException("Could not signal the synthetic worker.");
        await signal.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, signal.ExitCode);
    }

    private static async Task<string> RestrictedWorkerConnection(IntegrationDatabase database)
    {
        var role = $"nfr13_worker_{Guid.NewGuid():N}";
        var password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await using var sql = new NpgsqlConnection(database.ConnectionString);
        await sql.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE ROLE {role} LOGIN INHERIT PASSWORD '{password}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS; GRANT nexoruta_worker TO {role};", sql);
        await command.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(database.ConnectionString) { Username = role, Password = password, Pooling = false }.ConnectionString;
    }
}
