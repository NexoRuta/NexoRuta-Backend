using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.IntegrationTests.Fixtures;

public sealed class IntegrationApiFactory(IntegrationDatabase database, string environment = "Testing",
    IDictionary<string, string?>? settings = null, IInterceptor[]? interceptors = null,
    bool replaceDbContext = true, ICorreoSender? correoSender = null,
    bool seedOperationalRules = true, TimeProvider? timeProvider = null) : WebApplicationFactory<NexoRuta.Api.Controllers.AuthController>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Minimal hosting reads configuration before ConfigureAppConfiguration runs.
        var overrides = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = database.ConnectionString,
            ["OpenTelemetry:Enabled"] = "false",
            ["DatabaseInitialization:ApplyMigrations"] = "false",
            ["DatabaseInitialization:SeedData"] = "true",
            ["AccesoInicial:UsuarioEmail"] = $"owner-{Guid.NewGuid():N}@example.test",
            ["AccesoInicial:OperadorUsuarioEmail"] = $"operator-{Guid.NewGuid():N}@example.test",
            ["AccesoInicial:OperadorNombre"] = "Integration operator",
            ["AccesoInicial:ComercioNombre"] = "Integration commerce",
            ["Tracking:PublicBaseUrl"] = "https://tracking.example.test",
            ["PasswordRecovery:ResetPageUrl"] = "https://commerce.example.test/restablecer-contrasena",
            ["PasswordRecovery:TokenLifespanSeconds"] = "900",
            ["Email:Smtp:Host"] = "mailpit.integration.test",
            ["Email:Smtp:Port"] = "1025",
            ["Email:Smtp:UseSsl"] = "false",
            ["Email:Smtp:FromAddress"] = "noreply@example.test",
            ["Email:Smtp:FromName"] = "NexoRuta integration",
            ["DataProtection:KeysDirectory"] = Path.Combine(Path.GetTempPath(), "NexoRuta-Integration-Keys"),
        };
        var bootstrapSettings = AuthenticationTestData.BootstrapSettings(
            new NexoRuta.Infrastructure.Administracion.AccesoInicial("", "", "", ""));
        builder.ConfigureHostConfiguration(configuration =>
        {
            configuration.AddInMemoryCollection(overrides);
            configuration.AddInMemoryCollection(bootstrapSettings);
            if (settings is not null) configuration.AddInMemoryCollection(settings);
        });
        // Reapply test-owned values after appsettings.json/environment providers, too.
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(overrides);
            configuration.AddInMemoryCollection(bootstrapSettings);
            if (settings is not null) configuration.AddInMemoryCollection(settings);
        });
        var host = base.CreateHost(builder);
        if (seedOperationalRules && host.Services.GetRequiredService<IConfiguration>().GetValue<bool>("DatabaseInitialization:SeedData"))
            Task.Run(async () =>
            {
                await using var db = database.CreateDbContext();
                await ReglasEnvioTestData.SembrarInicialesAsync(db);
            }).GetAwaiter().GetResult();
        return host;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureTestServices(services =>
        {
            if (replaceDbContext)
            {
                // Replace the registration as well: startup migrations/seeding must use our database.
                services.RemoveAll<NexoRutaDbContext>();
                services.RemoveAll<DbContextOptions<NexoRutaDbContext>>();
                services.AddScoped(_ => database.CreateDbContext(interceptors ?? []));
            }
            if (correoSender is not null)
            {
                services.RemoveAll<ICorreoSender>();
                services.AddSingleton(correoSender);
            }
            if (timeProvider is not null)
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(timeProvider);
            }
        });
    }

    public HttpClient CreateHttpClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false
    });
}
