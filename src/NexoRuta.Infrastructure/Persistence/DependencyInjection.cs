using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexoRuta.Application.Administracion;
using NexoRuta.Application.Envios;
using NexoRuta.Infrastructure.Administracion;

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
        var demoFlag = configuration["Demo:Enabled"];
        var demoEnabled = false;
        if (demoFlag is not null && !bool.TryParse(demoFlag, out demoEnabled))
            throw new InvalidOperationException("Demo:Enabled must be true or false.");
        services.AddSingleton(new AccesoInicial(
            demoEnabled ? "demo@nexoruta.local" : ValorRequerido("AccesoInicial:UsuarioEmail"),
            ValorRequerido("AccesoInicial:OperadorNombre"),
            demoEnabled ? "Comercio Demo" : ValorRequerido("AccesoInicial:ComercioNombre"),
            ValorRequerido("AccesoInicial:OperadorUsuarioEmail")));
        services.AddScoped<IAccesosUsuarioRepository, EfAccesosUsuarioRepository>();
        services.AddScoped<IOperadoresRepository, EfOperadoresRepository>();
        services.AddScoped<IComerciosRepository, EfComerciosRepository>();
        services.AddScoped<IEnviosRepository, EfEnviosRepository>();
        services.AddScoped<DatosInicialesSeeder>();
        services.AddScoped<CrearEnvioUseCase>();
        services.AddScoped<ListarEnviosUseCase>();
        return services;

        string ValorRequerido(string clave)
            => !string.IsNullOrWhiteSpace(configuration[clave])
                ? configuration[clave]!.Trim()
                : throw new InvalidOperationException($"Falta {clave} para el acceso inicial.");
    }
}
