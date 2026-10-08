using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexoRuta.Infrastructure.Administracion;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.IntegrationTests;

public sealed class ReleaseConfigurationTests
{
    [Theory]
    [InlineData(null, "custom@example.test", "Custom Commerce")]
    [InlineData("false", "custom@example.test", "Custom Commerce")]
    [InlineData("true", "demo@nexoruta.local", "Comercio Demo")]
    public void DemoRequiresExplicitTrue(string? enabled, string email, string commerce)
    {
        using var services = Build(enabled);
        var access = services.GetRequiredService<AccesoInicial>();
        Assert.Equal(email, access.UsuarioEmail);
        Assert.Equal(commerce, access.ComercioNombre);
        Assert.Equal("operator@example.test", access.OperadorUsuarioEmail);
        Assert.Equal("Custom Operator", access.OperadorNombre);
    }

    [Fact]
    public void InvalidDemoFlagFailsClosed() => Assert.Throws<InvalidOperationException>(() => Build("invalid"));

    [Fact]
    public void MissingNonDemoIdentityStillFails() => Assert.Throws<InvalidOperationException>(() => Build("false", null));

    private static ServiceProvider Build(string? enabled, string? email = "custom@example.test")
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=localhost;Database=unused",
            ["Demo:Enabled"] = enabled,
            ["AccesoInicial:UsuarioEmail"] = email,
            ["AccesoInicial:ComercioNombre"] = "Custom Commerce",
            ["AccesoInicial:OperadorNombre"] = "Custom Operator",
            ["AccesoInicial:OperadorUsuarioEmail"] = "operator@example.test"
        }).Build();
        return new ServiceCollection().AddNexoRutaPersistence(configuration).BuildServiceProvider();
    }
}
