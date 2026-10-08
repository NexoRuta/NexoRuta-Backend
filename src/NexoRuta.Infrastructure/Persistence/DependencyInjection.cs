using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexoRuta.Application.Envios;

namespace NexoRuta.Infrastructure.Persistence;

public static class DependencyInjection
{
    public static IServiceCollection AddNexoRutaPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("Falta ConnectionStrings:Postgres.");

        services.AddDbContext<NexoRutaDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<IEnviosRepository, EfEnviosRepository>();
        services.AddScoped<DemoDataSeeder>();
        services.AddScoped<CrearEnvioUseCase>();
        services.AddScoped<ObtenerContextoDemoUseCase>();
        services.AddScoped<ListarEnviosUseCase>();
        return services;
    }
}
