using Microsoft.EntityFrameworkCore;

namespace NexoRuta.Infrastructure.Persistence;

public static class NexoRutaDbContextOptionsExtensions
{
    public static DbContextOptionsBuilder UseNexoRutaPostgres(
        this DbContextOptionsBuilder options,
        string? connectionString = null) =>
        options.UseNpgsql(connectionString, postgres => postgres.UseNetTopologySuite());

    public static DbContextOptionsBuilder<TContext> UseNexoRutaPostgres<TContext>(
        this DbContextOptionsBuilder<TContext> options,
        string? connectionString = null)
        where TContext : DbContext =>
        options.UseNpgsql(connectionString, postgres => postgres.UseNetTopologySuite());
}
