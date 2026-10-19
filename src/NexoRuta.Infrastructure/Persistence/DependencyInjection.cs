using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
using NexoRuta.Domain.Administracion;
using System.Net.Mail;
using NexoRuta.Infrastructure.Correo;
using NexoRuta.Application.ZonasCobertura.Repositories;
using NexoRuta.Infrastructure.ZonasCobertura;
using NexoRuta.Application.ZonasCobertura.UseCases;
namespace NexoRuta.Infrastructure.Persistence;

public static class DependencyInjection
{
    public static IServiceCollection AddNexoRutaPersistence(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectionStringName = "Postgres")
    {
        var connectionString = configuration.GetConnectionString(connectionStringName)
            ?? throw new InvalidOperationException($"Falta ConnectionStrings:{connectionStringName}.");

        services.AddDbContext<NexoRutaDbContext>(options => options.UseNexoRutaPostgres(connectionString));
        services.AddMemoryCache();
        services.AddDataProtection();
        services.AddIdentityCore<Usuario>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.User.AllowedUserNameCharacters = string.Empty;
                options.Password.RequiredLength = 12;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequiredUniqueChars = 1;
            })
            .AddEntityFrameworkStores<NexoRutaDbContext>()
            .AddDefaultTokenProviders();
        services.Configure<DataProtectionTokenProviderOptions>(options =>
            options.TokenLifespan = TimeSpan.FromSeconds(configuration.GetValue("PasswordRecovery:TokenLifespanSeconds", 900)));
        var smtpSection = configuration.GetSection(ConfiguracionSmtp.ConfigurationSection);
        if (smtpSection.Exists())
        {
            services.AddOptions<ConfiguracionSmtp>().Bind(smtpSection)
                .Validate(options => !string.IsNullOrWhiteSpace(options.Host) && options.Port is > 0 and <= 65535
                    && MailAddress.TryCreate(options.FromAddress, out _)
                    && (string.IsNullOrWhiteSpace(options.Username) == string.IsNullOrWhiteSpace(options.Password)),
                    "Email:Smtp requires a host, valid port and from address; SMTP credentials must be provided together.")
                .ValidateOnStart();
            services.AddSingleton<ICorreoSender, SmtpCorreoSender>();
        }
        services.AddScoped<IRecuperacionPassword, ServicioRecuperacionPassword>();
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
        services.AddScoped<IZonasCoberturaRepository, EfZonasCoberturaRepository>();
        services.AddScoped<DepartamentoCatalogImporter>();
        services.AddScoped<RequerirOperadorCoberturaUseCase>();
        services.AddScoped<ValidarTerritorioUruguayUseCase>();
        services.AddScoped<GestionarZonasCoberturaUseCase>();
        services.AddScoped<CrearZonaCoberturaUseCase>();
        services.AddScoped<ConsultarCoberturaUseCase>();
        services.AddScoped<IUsuariosOrganizacionRepository, EfUsuariosOrganizacionRepository>();
        services.AddScoped<IOrganizacionPropiaRepository, EfOrganizacionPropiaRepository>();
        services.AddScoped<IOrganizacionLifecycleRepository, EfOrganizacionLifecycleRepository>();
        services.AddScoped<IPermisosUsuarioRepository, EfPermisosUsuarioRepository>();
        services.AddScoped<IUsuariosLoginRepository, EfUsuariosLoginRepository>();
        services.AddScoped<IVerificadorPassword, VerificadorPassword>();
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<ITokensRenovacion, TokensRenovacion>();
        services.AddScoped<ISesionesMovilesRepository, EfSesionesMovilesRepository>();
        services.AddScoped<SesionesMovilesUseCase>();
        services.AddScoped<NexoRuta.Application.Reparto.Repositories.IRepartidoresRepository,
            NexoRuta.Infrastructure.Reparto.EfRepartidoresRepository>();
        services.AddScoped<NexoRuta.Application.Reparto.UseCases.GestionarRepartidoresUseCase>();
        services.AddScoped<NexoRuta.Application.Reparto.Repositories.IConfiguracionRepartoRepository,
            NexoRuta.Infrastructure.Reparto.EfConfiguracionRepartoRepository>();
        services.AddScoped<NexoRuta.Application.Reparto.UseCases.ConfigurarRepartoUseCase>();
        services.AddScoped<NexoRuta.Application.Reparto.Repositories.IRutasRepository, NexoRuta.Infrastructure.Reparto.EfRutasRepository>();
        services.AddScoped<NexoRuta.Application.Reparto.UseCases.GestionarRutasUseCase>();
        services.AddScoped<NexoRuta.Application.Reparto.Repositories.IOperacionesRepartoRepository, NexoRuta.Infrastructure.Reparto.EfOperacionesRepartoRepository>();
        services.AddScoped<NexoRuta.Application.Reparto.UseCases.EjecutarRepartoUseCase>();
        services.AddScoped<NexoRuta.Application.Notificaciones.Repositories.INotificacionesRepartoRepository, NexoRuta.Infrastructure.Notificaciones.EfNotificacionesRepartoRepository>();
        services.AddScoped<NexoRuta.Infrastructure.Notificaciones.CifradorRegistroPush>();
        services.AddScoped<NexoRuta.Application.Notificaciones.UseCases.NotificacionesRepartoUseCase>();


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
