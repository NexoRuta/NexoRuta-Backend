using System.Security.Cryptography;
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
    private PostgreSqlContainer container = null!;

    public async Task InitializeAsync()
    {
        container = new PostgreSqlBuilder("postgres:18-alpine")
            .WithDatabase(initialDatabase)
            .WithUsername(username)
            .WithPassword(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)))
            .Build();
        try
        {
            await container.StartAsync();
        }
        catch
        {
            await container.DisposeAsync();
            throw;
        }
    }

    public async Task<IntegrationDatabase> CreateDatabaseAsync(string? targetMigration = null)
    {
        Assert.False(string.IsNullOrWhiteSpace(container.Id));
        Assert.Equal("Running", container.State.ToString());
        var source = new NpgsqlConnectionStringBuilder(container.GetConnectionString());
        Assert.Equal(initialDatabase, source.Database);
        Assert.Equal(username, source.Username);
        await using var owned = new NpgsqlConnection(source.ConnectionString);
        await owned.OpenAsync();
        await using (var identity = new NpgsqlCommand(
            "SELECT current_database(), current_user, current_setting('server_version_num')::int", owned))
        await using (var reader = await identity.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(initialDatabase, reader.GetString(0));
            Assert.Equal(username, reader.GetString(1));
            Assert.InRange(reader.GetInt32(2), 180000, 189999);
        }
        // Only this owned container can supply a connection; external settings are ignored.
        var connection = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Database = $"integration_{Guid.NewGuid():N}",
            Pooling = false
        };
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{connection.Database}\"", owned))
            await create.ExecuteNonQueryAsync();
        await using (var target = new NpgsqlConnection(connection.ConnectionString))
        {
            await target.OpenAsync();
            await using var identity = new NpgsqlCommand("SELECT current_database(), current_user", target);
            await using var reader = await identity.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(connection.Database, reader.GetString(0));
            Assert.Equal(username, reader.GetString(1));
        }
        var database = new IntegrationDatabase(connection.ConnectionString);
        await using var db = database.CreateDbContext();
        await db.Database.MigrateAsync(targetMigration);
        return database;
    }

    // All per-test databases disappear with the owned container. No database deletion SQL.
    public Task DisposeAsync() => container.DisposeAsync().AsTask();
}

public sealed class IntegrationDatabase
{
    private readonly DbContextOptions<NexoRutaDbContext> options;

    internal IntegrationDatabase(string connectionString)
    {
        options = new DbContextOptionsBuilder<NexoRutaDbContext>()
            .UseNpgsql(connectionString)
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
