
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NexoRuta.Infrastructure.Persistence;

public sealed class NexoRutaDbContextFactory
    : IDesignTimeDbContextFactory<NexoRutaDbContext>
{
    public NexoRutaDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<NexoRutaDbContext>()
            .UseNexoRutaPostgres()
            .Options;

        return new NexoRutaDbContext(options);
    }
}
