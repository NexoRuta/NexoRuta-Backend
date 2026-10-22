using NexoRuta.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace NexoRuta.IntegrationTests;

public sealed class DatabaseInitializationTests
{
    [Theory]
    [InlineData(null, null, false)]
    [InlineData("false", "false", false)]
    [InlineData("true", "false", true)]
    [InlineData("false", "true", true)]
    public void StartupOnlyAccessesDatabaseWhenExplicitlyEnabled(
        string? migrations, string? seed, bool accessesDatabase)
    {
        using var factory = new StartupFactory(migrations, seed);
        if (accessesDatabase)
        {
            var error = Record.Exception(() => factory.CreateClient());
            Assert.IsType<NpgsqlException>(error is NpgsqlException ? error : error?.InnerException);
        }
        else
        {
            using var client = factory.CreateClient();
            Assert.NotNull(client);
        }
    }

    private sealed class StartupFactory(string? migrations, string? seed) : WebApplicationFactory<NexoRuta.Api.Controllers.AuthController>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureHostConfiguration(configuration =>
            {
                var settings = new Dictionary<string, string?>
                {
                    // ponytail: a closed loopback port proves startup does not require a database.
                    ["ConnectionStrings:Postgres"] = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1;Pooling=false",
                    ["OpenTelemetry:Enabled"] = "false",
                    ["PasswordRecovery:ResetPageUrl"] = "https://commerce.example.test/restablecer-contrasena",
                    ["PasswordRecovery:TokenLifespanSeconds"] = "900",
                    ["Email:Smtp:Host"] = "mailpit.integration.test",
                    ["Email:Smtp:Port"] = "1025",
                    ["Email:Smtp:FromAddress"] = "noreply@example.test",
                    ["AccesoInicial:UsuarioEmail"] = "owner@example.test",
                    ["AccesoInicial:OperadorUsuarioEmail"] = "operator@example.test",
                    ["AccesoInicial:OperadorNombre"] = "Operator",
                    ["AccesoInicial:ComercioNombre"] = "Commerce",
                };
                if (migrations is not null) settings["DatabaseInitialization:ApplyMigrations"] = migrations;
                if (seed is not null) settings["DatabaseInitialization:SeedData"] = seed;
                configuration.AddInMemoryCollection(AuthenticationTestData.BootstrapSettings(new NexoRuta.Infrastructure.Administracion.AccesoInicial("", "", "", "")));
                configuration.AddInMemoryCollection(settings);
            });
            return base.CreateHost(builder);
        }
    }
}
