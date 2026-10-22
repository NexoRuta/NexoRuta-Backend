using System.Security.Cryptography;
using System.Text;
using NexoRuta.Application.Administracion.Interfaces;

namespace NexoRuta.Infrastructure.Administracion.Autenticacion;

public sealed class TokensRenovacion : ITokensRenovacion
{
    public (string Token, string Hash) Crear()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (token, CalcularHash(token)!);
    }

    public string? CalcularHash(string token)
        => token is { Length: 43 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))
            : null;
}
