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
using Microsoft.AspNetCore.Identity;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.UseCases;
using NexoRuta.Infrastructure.Administracion.Autenticacion;
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
        services.AddSingleton(_ =>
        {
            if (!configuration.GetValue<bool>("DatabaseInitialization:SeedData"))
                return new AccesoInicial("", "", "", "");
            return new AccesoInicial(Value("UsuarioEmail"), Value("OperadorNombre"), Value("ComercioNombre"), Value("OperadorUsuarioEmail"))
            {
                OperadorId = OrganizationId("OperadorId"),
                ComercioId = OrganizationId("ComercioId"),
                OperadorRolCodigo = Value("OperadorRolCodigo"),
                ComercioRolCodigo = Value("ComercioRolCodigo")
            };
        });
        services.AddScoped<IPlataformaRepository, EfPlataformaRepository>();
        services.AddScoped<IAltaPropiaRepository, EfPlataformaRepository>();
        services.AddScoped<IRegistroRepository, EfRegistroRepository>();
        services.AddScoped<IRolesRepository, EfRolesRepository>();
        services.AddScoped<IAccesosUsuarioRepository, EfAccesosUsuarioRepository>();
        services.AddScoped<IOperadoresRepository, EfOperadoresRepository>();
        services.AddScoped<IComerciosRepository, EfComerciosRepository>();
        services.AddScoped<IEnviosRepository, EfEnviosRepository>();
        services.AddScoped<IUsuariosOrganizacionRepository, EfUsuariosOrganizacionRepository>();
        services.AddScoped<IOrganizacionPropiaRepository, EfOrganizacionPropiaRepository>();
        services.AddScoped<IOrganizacionLifecycleRepository, EfOrganizacionLifecycleRepository>();
        services.AddScoped<IPermisosUsuarioRepository, EfPermisosUsuarioRepository>();
        services.AddScoped<IUsuariosLoginRepository, EfUsuariosLoginRepository>();
        services.AddScoped<IVerificadorPassword, VerificadorPassword>();


        services.AddScoped<GestionarRolesUseCase>();
        services.AddScoped<ListarUsuariosUseCase>();
        services.AddScoped<ListarEnviosUseCase>();
        services.AddScoped<CrearEnvioUseCase>();
        services.AddScoped<CrearUsuarioUseCase>();
        services.AddScoped<IniciarSesionUseCase>();
        services.AddScoped<CambiarEstadoUsuarioUseCase>();
        services.AddScoped<CambiarEmailUsuarioUseCase>();
        services.AddScoped<ObtenerPermisosActualesUseCase>();
        services.AddScoped<EsPropietarioOrganizacionUseCase>();
        services.AddScoped<ConsultarOrganizacionPropiaUseCase>();
        services.AddScoped<EditarNombreOrganizacionPropiaUseCase>();
        services.AddScoped<GestionarBajasOrganizacionUseCase>();
        services.AddScoped<GestionarReactivacionesOrganizacionUseCase>();




        services.AddSingleton<IPasswordHasher<string>, PasswordHasher<string>>();
        services.AddSingleton<IGeneradorPasswordHash, GeneradorPasswordHash>();

        services.AddScoped<DatosInicialesSeeder>();

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

        string Value(string key) => !string.IsNullOrWhiteSpace(configuration[$"AccesoInicial:{key}"])
            ? configuration[$"AccesoInicial:{key}"]!.Trim()
            : throw new InvalidOperationException($"Falta AccesoInicial:{key} para el acceso inicial.");

        Guid OrganizationId(string key)
            => Guid.TryParse(configuration[$"AccesoInicial:{key}"], out var id) && id != Guid.Empty
                ? id : throw new InvalidOperationException($"AccesoInicial:{key} must be a nonempty organization UUID when bootstrap is enabled.");
    }
}
