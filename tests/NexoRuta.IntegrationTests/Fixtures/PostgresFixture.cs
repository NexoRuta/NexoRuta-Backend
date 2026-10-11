using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Infrastructure.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;

namespace NexoRuta.IntegrationTests.Fixtures;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase($"integration_{Guid.NewGuid():N}")
        .WithUsername($"test_{Guid.NewGuid():N}")
        .WithPassword(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)))
        .Build();

    public async Task InitializeAsync()
    {
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

    public async Task<IntegrationDatabase> CreateDatabaseAsync()
    {
        // Only this owned container can supply a connection; external settings are ignored.
        var connection = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Database = $"integration_{Guid.NewGuid():N}",
            Pooling = false
        };
        var database = new IntegrationDatabase(connection.ConnectionString);
        await using var db = database.CreateDbContext();
        await db.Database.MigrateAsync();
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

    public NexoRutaDbContext CreateDbContext() => new(options);

    public IntegrationApiFactory CreateApiFactory() => new(this);
}
