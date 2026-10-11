
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using NexoRuta.Infrastructure.Seguimiento.Persistence;

namespace NexoRuta.Infrastructure.Seguimiento.Security;

public sealed record TokenProtegido(
    byte[] TokenCifrado,
    byte[] Nonce,
    byte[] Tag);

public sealed class CifradorTokenSeguimiento
{
    private readonly byte[] clave;

    public CifradorTokenSeguimiento(IConfiguration configuration)
    {
        var claveBase64 = configuration["Seguimiento:EncryptionKey"];

        if (string.IsNullOrWhiteSpace(claveBase64))
            throw new InvalidOperationException(
                "Falta Seguimiento:EncryptionKey.");

        try
        {
            clave = Convert.FromBase64String(claveBase64);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                "La clave de seguimiento no es Base64 válido.", ex);
        }

        if (clave.Length != 32)
            throw new InvalidOperationException(
                "La clave de seguimiento debe tener 32 bytes.");
    }

    public TokenProtegido Cifrar(
        string token,
        Guid operadorId,
        Guid envioId,
        string tokenHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var nonce = RandomNumberGenerator.GetBytes(12);
        var textoPlano = Encoding.UTF8.GetBytes(token);
        var textoCifrado = new byte[textoPlano.Length];
        var tag = new byte[16];

        var datosAsociados = CrearDatosAsociados(
            operadorId, envioId, tokenHash);

        try
        {
            using var aes = new AesGcm(clave, 16);

            aes.Encrypt(
                nonce,
                textoPlano,
                textoCifrado,
                tag,
                datosAsociados);

            return new TokenProtegido(
                textoCifrado,
                nonce,
                tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(textoPlano);
        }
    }

    public string Descifrar(EntregaSeguimiento entrega)
    {
        ArgumentNullException.ThrowIfNull(entrega);

        var textoPlano = new byte[entrega.TokenCifrado.Length];

        var datosAsociados = CrearDatosAsociados(
            entrega.OperadorId,
            entrega.EnvioId,
            entrega.TokenHash);

        try
        {
            using var aes = new AesGcm(clave, 16);

            aes.Decrypt(
                entrega.Nonce,
                entrega.TokenCifrado,
                entrega.Tag,
                textoPlano,
                datosAsociados);

            return Encoding.UTF8.GetString(textoPlano);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(textoPlano);
        }
    }

    private static byte[] CrearDatosAsociados(
        Guid operadorId,
        Guid envioId,
        string tokenHash)
    {
        return Encoding.UTF8.GetBytes(
            $"{operadorId:D}|{envioId:D}|{tokenHash}");
    }
}
