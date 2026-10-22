
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using NexoRuta.Infrastructure.Seguimiento.Persistence;
using NexoRuta.Infrastructure.Seguimiento.Security;

namespace NexoRuta.UnitTests.Seguimiento;

public sealed class CifradorTokenSeguimientoTests
{
    [Fact]
    public void CifrarYDescifrar_DebeRecuperarTokenOriginal()
    {
        var cifrador = CrearCifrador();
        var operadorId = Guid.NewGuid();
        var envioId = Guid.NewGuid();
        var token = Convert.ToHexString(
            RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        var tokenHash = CalcularHash(token);

        var protegido = cifrador.Cifrar(
            token, operadorId, envioId, tokenHash);

        var entrega = new EntregaSeguimiento(
            operadorId,
            envioId,
            tokenHash,
            protegido.TokenCifrado,
            protegido.Nonce,
            protegido.Tag);

        var recuperado = cifrador.Descifrar(entrega);

        Assert.Equal(token, recuperado);
        Assert.Equal(12, entrega.Nonce.Length);
        Assert.Equal(16, entrega.Tag.Length);
    }

    [Fact]
    public void Descifrar_ConTextoCifradoModificado_DebeFallar()
    {
        var (cifrador, entrega) = CrearEntrega();

        // Simulamos la alteración de un byte almacenado.
        entrega.TokenCifrado[0] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(
            () => cifrador.Descifrar(entrega));
    }

    [Fact]
    public void Descifrar_ConOtroEnvioId_DebeFallar()
    {
        var (cifrador, entrega) = CrearEntrega();

        // Mismo contenido cifrado, pero asociado a otro envío.
        var entregaAlterada = new EntregaSeguimiento(
            entrega.OperadorId,
            Guid.NewGuid(),
            entrega.TokenHash,
            entrega.TokenCifrado,
            entrega.Nonce,
            entrega.Tag);

        Assert.ThrowsAny<CryptographicException>(
            () => cifrador.Descifrar(entregaAlterada));
    }

    [Fact]
    public void CrearCifrador_SinClave_DebeFallar()
    {
        var configuracion = new ConfigurationBuilder().Build();

        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = new CifradorTokenSeguimiento(configuracion);
        });
    }

    private static CifradorTokenSeguimiento CrearCifrador()
    {
        var clave = RandomNumberGenerator.GetBytes(32);

        var configuracion = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Seguimiento:EncryptionKey"] =
                        Convert.ToBase64String(clave)
                })
            .Build();

        return new CifradorTokenSeguimiento(configuracion);
    }

    private static (CifradorTokenSeguimiento, EntregaSeguimiento)
        CrearEntrega()
    {
        var cifrador = CrearCifrador();
        var operadorId = Guid.NewGuid();
        var envioId = Guid.NewGuid();

        var token = Convert.ToHexString(
            RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        var tokenHash = CalcularHash(token);

        var protegido = cifrador.Cifrar(
            token, operadorId, envioId, tokenHash);

        var entrega = new EntregaSeguimiento(
            operadorId,
            envioId,
            tokenHash,
            protegido.TokenCifrado,
            protegido.Nonce,
            protegido.Tag);

        return (cifrador, entrega);
    }

    private static string CalcularHash(string token)
    {
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(token)))
            .ToLowerInvariant();
    }
}
