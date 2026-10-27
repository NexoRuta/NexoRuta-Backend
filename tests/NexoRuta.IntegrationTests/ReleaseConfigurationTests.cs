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
    [Fact]
    public void DisabledBootstrapIgnoresMalformedAndMissingConfiguration()
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1;Pooling=false",
            ["DatabaseInitialization:SeedData"] = "false",
            ["DatabaseInitialization:ApplyMigrations"] = "false",
            ["AccesoInicial:OperadorId"] = "not-a-guid",
            ["AccesoInicial:ComercioId"] = "not-a-guid"
        };
        using var provider = Build(values);
        var access = provider.GetRequiredService<AccesoInicial>();
        Assert.Null(access.OperadorId);
        Assert.Null(access.ComercioId);
        Assert.Equal("", access.UsuarioEmail);
    }

    [Fact]
    public void BootstrapUsesExplicitValues()
    {
        using var services = Build(Values());
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
        var values = Values();
        values[$"AccesoInicial:{field}"] = value;
        using var services = Build(values);
        var exception = Assert.Throws<InvalidOperationException>(() => services.GetRequiredService<AccesoInicial>());
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
        var configuration = Configuration(values);
        services.AddSingleton<IConfiguration>(configuration);
        services.AddNexoRutaPersistence(configuration);
        services.AddScoped<IUsuarioActual, UsuarioActualHttp>();
        Assert.Equal(ServiceLifetime.Singleton, services.Single(x => x.ServiceType == typeof(AccesoInicial)).Lifetime);
        Type[] scoped = [typeof(IUsuarioActual), typeof(IAccesosUsuarioRepository),
            typeof(IOperadoresRepository), typeof(IComerciosRepository), typeof(IEnviosRepository),
            typeof(CrearEnvioUseCase), typeof(ListarEnviosUseCase), typeof(DatosInicialesSeeder)];
        foreach (var type in scoped)
            Assert.Equal(ServiceLifetime.Scoped, services.Single(x => x.ServiceType == type).Lifetime);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = provider.CreateScope();
        var access = provider.GetRequiredService<AccesoInicial>();
        Assert.Equal(new AccesoInicial("custom@example.test", "Custom Operator", "Custom Commerce", "operator@example.test")
        {
            OperadorId = Guid.Parse(values["AccesoInicial:OperadorId"]!),
            ComercioId = Guid.Parse(values["AccesoInicial:ComercioId"]!),
            OperadorRolCodigo = values["AccesoInicial:OperadorRolCodigo"]!.Trim(),
            ComercioRolCodigo = values["AccesoInicial:ComercioRolCodigo"]!.Trim()
        }, access);
        Assert.IsType<UsuarioActualHttp>(scope.ServiceProvider.GetRequiredService<IUsuarioActual>());
        Assert.IsType<EfAccesosUsuarioRepository>(scope.ServiceProvider.GetRequiredService<IAccesosUsuarioRepository>());
        Assert.IsType<EfOperadoresRepository>(scope.ServiceProvider.GetRequiredService<IOperadoresRepository>());
        Assert.IsType<EfComerciosRepository>(scope.ServiceProvider.GetRequiredService<IComerciosRepository>());
        Assert.IsType<EfEnviosRepository>(scope.ServiceProvider.GetRequiredService<IEnviosRepository>());
        foreach (var type in scoped)
            Assert.Same(scope.ServiceProvider.GetRequiredService(type), scope.ServiceProvider.GetRequiredService(type));
    }

    [Theory]
    [InlineData("OperadorId", null)]
    [InlineData("OperadorId", "not-a-guid")]
    [InlineData("OperadorId", "00000000-0000-0000-0000-000000000000")]
    [InlineData("ComercioId", null)]
    [InlineData("ComercioId", "not-a-guid")]
    [InlineData("ComercioId", "00000000-0000-0000-0000-000000000000")]
    public void EnabledBootstrapValidatesOrganizationIdsOnlyWhenResolved(string field, string? value)
    {
        var values = Values();
        values[$"AccesoInicial:{field}"] = value;
        using var services = Build(values); // Registration itself must never bind bootstrap configuration.
        var error = Assert.Throws<InvalidOperationException>(() => services.GetRequiredService<AccesoInicial>());
        Assert.Contains($"AccesoInicial:{field}", error.Message);
    }

    private static Dictionary<string, string?> Values() => new()
    {
        ["ConnectionStrings:Postgres"] = "Host=localhost;Database=unused",
        ["DatabaseInitialization:SeedData"] = "true",
        ["AccesoInicial:OperadorId"] = Guid.NewGuid().ToString(),
        ["AccesoInicial:ComercioId"] = Guid.NewGuid().ToString(),
        ["AccesoInicial:UsuarioEmail"] = "custom@example.test",
        ["AccesoInicial:ComercioNombre"] = "Custom Commerce",
        ["AccesoInicial:OperadorNombre"] = "Custom Operator",
        ["AccesoInicial:OperadorUsuarioEmail"] = "operator@example.test",
        ["AccesoInicial:OperadorRolCodigo"] = "owner-operator",
        ["AccesoInicial:ComercioRolCodigo"] = "owner-commerce"
    };

    private static IConfiguration Configuration(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static ServiceProvider Build(Dictionary<string, string?> values)
        => new ServiceCollection().AddNexoRutaPersistence(Configuration(values)).BuildServiceProvider();
}
