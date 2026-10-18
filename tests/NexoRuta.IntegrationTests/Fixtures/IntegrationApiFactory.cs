using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.IntegrationTests.Fixtures;

public sealed class IntegrationApiFactory(IntegrationDatabase database, string environment = "Testing",
    IDictionary<string, string?>? settings = null, IInterceptor[]? interceptors = null,
    bool replaceDbContext = true) : WebApplicationFactory<Program>
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
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureTestServices(services =>
        {
            if (!replaceDbContext) return;
            // Replace the registration as well: startup migrations/seeding must use our database.
            services.RemoveAll<NexoRutaDbContext>();
            services.RemoveAll<DbContextOptions<NexoRutaDbContext>>();
            services.AddScoped(_ => database.CreateDbContext(interceptors ?? []));
        });
    }

    public HttpClient CreateHttpClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false
    });
}
