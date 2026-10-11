using System.Security.Cryptography;
using System.Text;

namespace NexoRuta.Application.Seguimiento.Services;

public static class GeneradorTokenSeguimiento
{
    public static string Generar()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(32);

        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string CalcularHash(string token)
    {
        if (string.IsNullOrWhiteSpace(token)
            || token.Length != 64
            || token.Any(c => !Uri.IsHexDigit(c)))
        {
            throw new ArgumentException(
                "El token debe contener 64 caracteres hexadecimales.",
                nameof(token));
        }

        byte[] contenido = Encoding.ASCII.GetBytes(token);

        byte[] hash = SHA256.HashData(contenido);

        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}