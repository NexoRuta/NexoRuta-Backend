using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Application.Envios.Repositories;
using NexoRuta.Application.Envios.UseCases;
using NexoRuta.Infrastructure.Administracion;
using NexoRuta.Infrastructure.Administracion.Repositories;
using NexoRuta.Infrastructure.Envios.Repositories;
using NexoRuta.Application.Seguimiento.Interfaces;
using NexoRuta.Infrastructure.Seguimiento.Consultas;
using NexoRuta.Application.Seguimiento.UseCases;
using NexoRuta.Infrastructure.Seguimiento.Repositories;

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
        services.AddSingleton(new AccesoInicial(
            ValorRequerido("AccesoInicial:UsuarioEmail"),
            ValorRequerido("AccesoInicial:OperadorNombre"),
            ValorRequerido("AccesoInicial:ComercioNombre"),
            ValorRequerido("AccesoInicial:OperadorUsuarioEmail")));

        services.AddScoped<IAccesosUsuarioRepository, EfAccesosUsuarioRepository>();
        services.AddScoped<IOperadoresRepository, EfOperadoresRepository>();
        services.AddScoped<IComerciosRepository, EfComerciosRepository>();
        services.AddScoped<IEnviosRepository, EfEnviosRepository>();
        services.AddScoped<DatosInicialesSeeder>();
        services.AddScoped<CrearEnvioUseCase>();
        services.AddScoped<ListarEnviosUseCase>();

        // M05 - Seguimiento
        services.AddScoped<
            IConsultaEstadoEnvio,
            EfConsultaEstadoEnvio>();

        services.AddScoped<
            IEnlacesSeguimientoRepository,
            EfEnlacesSeguimientoRepository>();

        services.AddScoped<CrearEnlaceSeguimientoUseCase>();

        services.AddScoped<ConsultarSeguimientoPublicoUseCase>();
        services.AddScoped<RecuperarEnlaceSeguimiento>();


        // Fin M05


        return services;

        string ValorRequerido(string clave)
            => !string.IsNullOrWhiteSpace(configuration[clave])
                ? configuration[clave]!.Trim()
                : throw new InvalidOperationException($"Falta {clave} para el acceso inicial.");
    }
}
