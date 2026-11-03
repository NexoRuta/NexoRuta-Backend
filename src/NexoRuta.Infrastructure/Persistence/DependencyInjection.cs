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
using NexoRuta.Application.Tarifas.Repositories;
using NexoRuta.Application.Tarifas.UseCases;
using NexoRuta.Infrastructure.Tarifas;
using NexoRuta.Application.ReglasOperativas.Repositories;
using NexoRuta.Application.ReglasOperativas.UseCases;
using NexoRuta.Infrastructure.ReglasOperativas;
using NexoRuta.Infrastructure.Caching;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using StackExchange.Redis;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
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
        var cacheSettings = DistributedCacheSettings.Read(configuration);
        services.AddSingleton(cacheSettings);
        if (cacheSettings.Enabled)
        {
            var redisConnection = ConfigurationOptions.Parse(configuration.GetConnectionString("Redis")!);
            redisConnection.Password = cacheSettings.RedisPassword;
            redisConnection.AbortOnConnectFail = false;
            redisConnection.ConnectRetry = 1;
            redisConnection.ConnectTimeout = cacheSettings.OperationTimeoutMilliseconds;
            redisConnection.SyncTimeout = cacheSettings.OperationTimeoutMilliseconds;
            redisConnection.AsyncTimeout = cacheSettings.OperationTimeoutMilliseconds;
            redisConnection.KeepAlive = 30;
            redisConnection.AllowAdmin = false;
            if (cacheSettings.RedisTls)
            {
                redisConnection.Ssl = true;
                var trustedCertificates = cacheSettings.LoadTrustedCertificates();
                redisConnection.CertificateValidation += (_, certificate, _, errors) =>
                    ValidarCertificadoRedis(certificate, errors, trustedCertificates);
            }
            services.AddStackExchangeRedisCache(options => options.ConfigurationOptions = redisConnection);
        }
        services.AddSingleton(provider => new DistributedCacheAccess(
            provider.GetService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>(),
            cacheSettings,
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DistributedCacheAccess>>()));
        services.AddScoped<IConsultaIdentidadVisualOperador, EfConsultaIdentidadVisualOperador>();
        services.AddSingleton(NexoRuta.Infrastructure.Reparto.GeoapifyRoutingOptions.Leer(configuration));
        services.AddHttpClient(NexoRuta.Infrastructure.Reparto.GeoapifyRouting.ClienteHttp,h=>h.Timeout=Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(()=>new SocketsHttpHandler{AllowAutoRedirect=false,MaxConnectionsPerServer=2})
            .RemoveAllLoggers();
        services.AddScoped<NexoRuta.Infrastructure.Reparto.IConsumoRouting,NexoRuta.Infrastructure.Reparto.ConsumoRouting>();
        services.AddScoped<NexoRuta.Application.Reparto.Estimaciones.IProveedorTramosViales,NexoRuta.Infrastructure.Reparto.GeoapifyRouting>();
        services.AddHttpClient(NexoRuta.Infrastructure.Envios.Geocodificacion.GeocodificadorGeoapify.ClienteHttp,
            http => http.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, MaxConnectionsPerServer = 2 })
            .RemoveAllLoggers();
        services.AddSingleton(NexoRuta.Infrastructure.Envios.Geocodificacion.OpcionesGeocodificacion.Leer(configuration));
        services.AddScoped<NexoRuta.Infrastructure.Envios.Geocodificacion.IConsumoGeocodificacion, NexoRuta.Infrastructure.Envios.Geocodificacion.ConsumoGeocodificacion>();
        services.AddScoped<NexoRuta.Application.Envios.Interfaces.IGeocodificador, NexoRuta.Infrastructure.Envios.Geocodificacion.GeocodificadorGeoapify>();
        services.AddScoped<NexoRuta.Application.Envios.Repositories.IDestinosEnvioRepository, NexoRuta.Infrastructure.Envios.Repositories.EfDestinosEnvioRepository>();
        services.AddScoped<NexoRuta.Application.Envios.UseCases.GestionarDestinoEnvioUseCase>();
        services.AddHttpClient(NexoRuta.Infrastructure.Envios.Geocodificacion.GeocodificadorMapboxTemporal.ClienteHttp,
            http => http.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, MaxConnectionsPerServer = 2 })
            .RemoveAllLoggers();
        services.AddScoped<NexoRuta.Infrastructure.Envios.Geocodificacion.GeocodificadorMapboxTemporal>();
        services.AddKeyedScoped<NexoRuta.Application.Envios.UseCases.GestionarDestinoEnvioUseCase>(
            NexoRuta.Infrastructure.Envios.Geocodificacion.GeocodificadorMapboxTemporal.Servicio, (provider, _) => new(
                provider.GetRequiredService<NexoRuta.Application.Administracion.Interfaces.IUsuarioActual>(),
                provider.GetRequiredService<NexoRuta.Application.Envios.Repositories.IDestinosEnvioRepository>(),
                provider.GetRequiredService<NexoRuta.Infrastructure.Envios.Geocodificacion.GeocodificadorMapboxTemporal>()));
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
        services.AddScoped<IServiciosEnvioRepository, EfServiciosEnvioRepository>();
        services.AddScoped<NexoRuta.Application.Seguimiento.Interfaces.ICancelacionPublica, NexoRuta.Infrastructure.Seguimiento.Processing.CancelacionPublica>();
        services.AddScoped<NexoRuta.Application.Envios.Repositories.ICancelacionesEnvioRepository, NexoRuta.Infrastructure.Envios.Repositories.EfCancelacionesEnvioRepository>();
        services.AddScoped<NexoRuta.Application.Envios.UseCases.CancelarEnvioUseCase>();
        services.AddScoped<IZonasCoberturaRepository, EfZonasCoberturaRepository>();
        services.AddScoped<ITarifariosRepository, EfTarifariosRepository>();
        services.AddScoped<IReglasOperativasRepository, EfReglasOperativasRepository>();
        services.AddScoped<ConsultarReglasOperativasUseCase>();
        services.AddScoped<CrearBorradorReglasOperativasUseCase>();
        services.AddScoped<EditarBorradorReglasOperativasUseCase>();
        services.AddScoped<PublicarBorradorReglasOperativasUseCase>();
        services.AddScoped<ConsultarTarifarioUseCase>();
        services.AddScoped<CrearBorradorTarifarioUseCase>();
        services.AddScoped<EditarBorradorTarifarioUseCase>();
        services.AddScoped<PublicarBorradorTarifarioUseCase>();
        services.AddScoped<SimularTarifaUseCase>();
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
        services.AddScoped<NexoRuta.Application.Envios.Importacion.ImportarEnviosUseCase>();
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
        services.AddScoped<DatosDemoSeeder>();

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

        static bool ValidarCertificadoRedis(X509Certificate? certificate, SslPolicyErrors errors,
            IReadOnlyList<X509Certificate2> trustedCertificates)
        {
            if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch
                    | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0)
                return false;
            using var candidate = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            foreach (var trusted in trustedCertificates)
                chain.ChainPolicy.CustomTrustStore.Add(trusted);
            return chain.Build(candidate);
        }

        string Value(string key) => !string.IsNullOrWhiteSpace(configuration[$"AccesoInicial:{key}"])
            ? configuration[$"AccesoInicial:{key}"]!.Trim()
            : throw new InvalidOperationException($"Falta AccesoInicial:{key} para el acceso inicial.");

        Guid OrganizationId(string key)
            => Guid.TryParse(configuration[$"AccesoInicial:{key}"], out var id) && id != Guid.Empty
                ? id : throw new InvalidOperationException($"AccesoInicial:{key} must be a nonempty organization UUID when bootstrap is enabled.");
    }
}
