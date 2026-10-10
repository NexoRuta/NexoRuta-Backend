using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexoRuta.Api.Core.Security;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Application.Envios.Repositories;
using NexoRuta.Application.Envios.UseCases;
using NexoRuta.Infrastructure.Administracion;
using NexoRuta.Infrastructure.Administracion.Repositories;
using NexoRuta.Infrastructure.Envios.Repositories;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.IntegrationTests;

public sealed class ReleaseConfigurationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("true")]
    [InlineData("invalid")]
    public void BootstrapUsesExplicitValuesRegardlessOfLegacySetting(string? legacySetting)
    {
        using var services = Build(Values(legacySetting));
        var access = services.GetRequiredService<AccesoInicial>();
        Assert.Equal("custom@example.test", access.UsuarioEmail);
        Assert.Equal("Custom Commerce", access.ComercioNombre);
        Assert.Equal("operator@example.test", access.OperadorUsuarioEmail);
        Assert.Equal("Custom Operator", access.OperadorNombre);
    }

    [Theory]
    [InlineData("UsuarioEmail", null)]
    [InlineData("UsuarioEmail", "")]
    [InlineData("UsuarioEmail", " ")]
    [InlineData("OperadorNombre", null)]
    [InlineData("OperadorNombre", "")]
    [InlineData("OperadorNombre", " ")]
    [InlineData("ComercioNombre", null)]
    [InlineData("ComercioNombre", "")]
    [InlineData("ComercioNombre", " ")]
    [InlineData("OperadorUsuarioEmail", null)]
    [InlineData("OperadorUsuarioEmail", "")]
    [InlineData("OperadorUsuarioEmail", " ")]
    public void BootstrapRequiresEachConfiguredValue(string field, string? value)
    {
        var values = Values("true");
        values[$"AccesoInicial:{field}"] = value;
        var exception = Assert.Throws<InvalidOperationException>(() => Build(values));
        Assert.Equal($"Falta AccesoInicial:{field} para el acceso inicial.", exception.Message);
    }

    [Fact]
    public void BootstrapAndScopedServicesResolveWithoutOpeningDatabase()
    {
        var values = Values();
        foreach (var key in values.Keys.Where(x => x.StartsWith("AccesoInicial:")).ToArray())
            values[key] = $"  {values[key]}  ";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddNexoRutaPersistence(Configuration(values));
        services.AddScoped<IUsuarioActual, UsuarioActualHttp>();
        Assert.Equal(ServiceLifetime.Singleton, services.Single(x => x.ServiceType == typeof(AccesoInicial)).Lifetime);
        Type[] scoped = [typeof(IUsuarioActual), typeof(IAccesosUsuarioRepository), typeof(IEnviosRepository),
            typeof(CrearEnvioUseCase), typeof(ListarEnviosUseCase), typeof(DatosInicialesSeeder)];
        foreach (var type in scoped)
            Assert.Equal(ServiceLifetime.Scoped, services.Single(x => x.ServiceType == type).Lifetime);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = provider.CreateScope();
        var access = provider.GetRequiredService<AccesoInicial>();
        Assert.Equal(new AccesoInicial("custom@example.test", "Custom Operator", "Custom Commerce", "operator@example.test"), access);
        Assert.IsType<UsuarioActualHttp>(scope.ServiceProvider.GetRequiredService<IUsuarioActual>());
        Assert.IsType<EfAccesosUsuarioRepository>(scope.ServiceProvider.GetRequiredService<IAccesosUsuarioRepository>());
        Assert.IsType<EfEnviosRepository>(scope.ServiceProvider.GetRequiredService<IEnviosRepository>());
        foreach (var type in scoped)
            Assert.Same(scope.ServiceProvider.GetRequiredService(type), scope.ServiceProvider.GetRequiredService(type));
    }

    private static Dictionary<string, string?> Values(string? legacySetting = null) => new()
    {
        ["ConnectionStrings:Postgres"] = "Host=localhost;Database=unused",
        // Regression input only: the retired setting must not alter bootstrap behavior.
        ["Demo:Enabled"] = legacySetting,
        ["AccesoInicial:UsuarioEmail"] = "custom@example.test",
        ["AccesoInicial:ComercioNombre"] = "Custom Commerce",
        ["AccesoInicial:OperadorNombre"] = "Custom Operator",
        ["AccesoInicial:OperadorUsuarioEmail"] = "operator@example.test"
    };

    private static IConfiguration Configuration(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static ServiceProvider Build(Dictionary<string, string?> values)
        => new ServiceCollection().AddNexoRutaPersistence(Configuration(values)).BuildServiceProvider();
}
