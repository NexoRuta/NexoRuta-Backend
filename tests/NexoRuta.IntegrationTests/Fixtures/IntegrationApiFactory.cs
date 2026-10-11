using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.IntegrationTests.Fixtures;

public sealed class IntegrationApiFactory(IntegrationDatabase database) : WebApplicationFactory<Program>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Minimal hosting reads configuration before ConfigureAppConfiguration runs.
        builder.ConfigureHostConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = database.ConnectionString,
                ["OpenTelemetry:Enabled"] = "false",
                ["AccesoInicial:UsuarioEmail"] = $"owner-{Guid.NewGuid():N}@example.test",
                ["AccesoInicial:OperadorUsuarioEmail"] = $"operator-{Guid.NewGuid():N}@example.test",
                ["AccesoInicial:OperadorNombre"] = "Integration operator",
                ["AccesoInicial:ComercioNombre"] = "Integration commerce"
            }));
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            // Replace the registration as well: startup migrations/seeding must use our database.
            services.RemoveAll<NexoRutaDbContext>();
            services.RemoveAll<DbContextOptions<NexoRutaDbContext>>();
            services.AddScoped(_ => database.CreateDbContext());
        });
    }

    public HttpClient CreateHttpClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false
    });
}
