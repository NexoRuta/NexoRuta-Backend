using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NexoRuta.Infrastructure.Persistence;

public sealed class NexoRutaDbContextFactory
    : IDesignTimeDbContextFactory<NexoRutaDbContext>
{
    public NexoRutaDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "ConnectionStrings__Postgres");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Falta la variable ConnectionStrings__Postgres.");
        }

        var options = new DbContextOptionsBuilder<NexoRutaDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new NexoRutaDbContext(options);
    }
}