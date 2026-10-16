using NexoRuta.Application.Administracion;
using NexoRuta.Domain.Administracion;

namespace NexoRuta.UnitTests.Administracion;

public sealed class ValidacionCredencialesTests
{
    [Fact]
    public void SharedRuleNormalizesEmailWithoutChangingPassword()
        => Assert.Equal("user@example.test", ValidacionCredenciales.Validar("  USER@example.test  ", "A-password-2026!"));

    [Theory]
    [InlineData("bad", "A-password-2026!")]
    [InlineData("", "A-password-2026!")]
    [InlineData("user@example.test", "short")]
    [InlineData("user@example.test", "            ")]
    public void InvalidCredentialsAreRejected(string email, string password)
        => Assert.Throws<ArgumentException>(() => ValidacionCredenciales.Validar(email, password));

    [Fact]
    public void PlatformCapabilityNeverAppearsInOrganizationCatalog()
    {
        foreach (var scope in Enum.GetValues<TipoAccesoUsuario>())
            Assert.False(CatalogoPermisos.EsAplicable(RolPlataforma.GestionarOrganizaciones, scope));
    }

    [Fact]
    public void PlatformRevocationVersionsOnlyStateTransitions()
    {
        var access = new AccesoPlataforma(new Usuario("platform@example.test", "test-hash"), new RolPlataforma());
        access.CambiarEstado(true);
        Assert.Equal(0, access.VersionSesion);
        access.CambiarEstado(false);
        access.CambiarEstado(false);
        Assert.Equal(1, access.VersionSesion);
        access.CambiarEstado(true);
        Assert.Equal(2, access.VersionSesion);
    }
}
