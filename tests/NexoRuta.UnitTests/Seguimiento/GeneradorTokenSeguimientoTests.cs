using NexoRuta.Application.Seguimiento.Services;

namespace NexoRuta.UnitTests.Seguimiento;

public sealed class GeneradorTokenSeguimientoTests
{
    [Fact]
    public void Generar_DebeCrearTokenHexadecimalDe64Caracteres()
    {
        // Act
        var token = GeneradorTokenSeguimiento.Generar();

        // Assert
        Assert.Matches("^[0-9a-f]{64}$", token);
    }

    [Fact]
    public void Generar_DebeCrearTokensDiferentes()
    {
        // Act
        var primerToken = GeneradorTokenSeguimiento.Generar();
        var segundoToken = GeneradorTokenSeguimiento.Generar();

        // Assert
        Assert.NotEqual(primerToken, segundoToken);
    }

    [Fact]
    public void CalcularHash_ConMismoToken_DebeDevolverMismoHash()
    {
        // Arrange
        var token = GeneradorTokenSeguimiento.Generar();

        // Act
        var primerHash = GeneradorTokenSeguimiento.CalcularHash(token);
        var segundoHash = GeneradorTokenSeguimiento.CalcularHash(token);

        // Assert
        Assert.Equal(primerHash, segundoHash);
        Assert.Matches("^[0-9a-f]{64}$", primerHash);
        Assert.NotEqual(token, primerHash);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("token-invalido")]
    public void CalcularHash_ConTokenInvalido_DebeLanzarExcepcion(
        string token)
    {
        // Act + Assert
        Assert.Throws<ArgumentException>(
            () => GeneradorTokenSeguimiento.CalcularHash(token));
    }
}