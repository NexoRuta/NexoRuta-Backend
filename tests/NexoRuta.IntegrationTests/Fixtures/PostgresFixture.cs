using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NexoRuta.Infrastructure.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;

namespace NexoRuta.IntegrationTests.Fixtures;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly string initialDatabase = $"integration_{Guid.NewGuid():N}";
    private readonly string username = $"test_{Guid.NewGuid():N}";
    private PostgreSqlContainer? container;

    public async Task InitializeAsync()
    {
        var createdContainer = new PostgreSqlBuilder("postgis/postgis:18-3.6-alpine")
            .WithDatabase(initialDatabase)
            .WithUsername(username)
            .WithPassword(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)))
            .Build();
        container = createdContainer;
        try
        {
            await MeasureAsync("start_container", async () => await createdContainer.StartAsync());
        }
        catch (Exception startException)
        {
            container = null;
            try
            {
                await createdContainer.DisposeAsync();
            }
            catch (Exception cleanupException)
            {
                startException.Data["PostgresFixture.CleanupFailureType"] = cleanupException.GetType().FullName;
            }
            throw;
        }
    }

    public async Task<IntegrationDatabase> CreateDatabaseAsync(string? targetMigration = null)
    {
        var startedContainer = container ?? throw new InvalidOperationException("The PostgreSQL fixture did not start.");
        Assert.False(string.IsNullOrWhiteSpace(startedContainer.Id));
        Assert.Equal("Running", startedContainer.State.ToString());
        var source = new NpgsqlConnectionStringBuilder(startedContainer.GetConnectionString());
        Assert.Equal(initialDatabase, source.Database);
        Assert.Equal(username, source.Username);
        await MeasureAsync("verify_container_identity", async () =>
        {
            await using var owned = new NpgsqlConnection(source.ConnectionString);
            await owned.OpenAsync();
            await using var identity = new NpgsqlCommand(
                "SELECT current_database(), current_user, current_setting('server_version_num')::int", owned);
            await using var reader = await identity.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(initialDatabase, reader.GetString(0));
            Assert.Equal(username, reader.GetString(1));
            Assert.InRange(reader.GetInt32(2), 180000, 189999);
        });
        // Only this owned container can supply a connection; external settings are ignored.
        var connection = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Database = $"integration_{Guid.NewGuid():N}",
            Pooling = false
        };
        await MeasureAsync("create_isolated_database", async () =>
        {
            await using var owned = new NpgsqlConnection(source.ConnectionString);
            await owned.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{connection.Database}\"", owned);
            await create.ExecuteNonQueryAsync();
        }, connection.Database);
        await MeasureAsync("verify_isolated_database", async () =>
        {
            await using var target = new NpgsqlConnection(connection.ConnectionString);
            await target.OpenAsync();
            await using var identity = new NpgsqlCommand("SELECT current_database(), current_user", target);
            await using var reader = await identity.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(connection.Database, reader.GetString(0));
            Assert.Equal(username, reader.GetString(1));
        }, connection.Database);
        var database = new IntegrationDatabase(connection.ConnectionString);
        await using var db = database.CreateDbContext();
        await MeasureAsync("apply_migrations", () => db.Database.MigrateAsync(targetMigration), connection.Database,
            targetMigration);
        return database;
    }

    private static async Task MeasureAsync(string phase, Func<Task> operation, string? database = null,
        string? targetMigration = null)
    {
        var timer = Stopwatch.StartNew();
        var status = "passed";
        try
        {
            await operation();
        }
        catch
        {
            status = "failed";
            throw;
        }
        finally
        {
            Console.WriteLine("NEXORUTA_FIXTURE_TIMING " + JsonSerializer.Serialize(new
            {
                phase,
                duration_ms = timer.ElapsedMilliseconds,
                status,
                database,
                target_migration = targetMigration
            }));
        }
    }

    // All per-test databases disappear with the owned container. No database deletion SQL.
    public Task DisposeAsync()
    {
        var current = container;
        container = null;
        return current is null ? Task.CompletedTask : current.DisposeAsync().AsTask();
    }
}

public sealed class IntegrationDatabase
{
    private readonly DbContextOptions<NexoRutaDbContext> options;

    internal IntegrationDatabase(string connectionString)
    {
        options = new DbContextOptionsBuilder<NexoRutaDbContext>()
            .UseNexoRutaPostgres(connectionString)
            .Options;
        ConnectionString = connectionString;
    }

    internal string ConnectionString { get; }

    public NexoRutaDbContext CreateDbContext(params IInterceptor[] interceptors)
    {
        if (interceptors.Length == 0)
            return new(options);

        var optionsWithInterceptors = new DbContextOptionsBuilder<NexoRutaDbContext>(options)
            .AddInterceptors(interceptors)
            .Options;
        return new(optionsWithInterceptors);
    }

    public IntegrationApiFactory CreateApiFactory(params IInterceptor[] interceptors) =>
        new(this, interceptors: interceptors);
}
