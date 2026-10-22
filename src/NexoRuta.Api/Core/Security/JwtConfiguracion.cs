
using Microsoft.IdentityModel.Tokens;

namespace NexoRuta.Api.Core.Security;

public sealed class JwtConfiguracion
{
    public string Issuer { get; }
    public string Audience { get; }
    public SymmetricSecurityKey SigningKey { get; }

    public JwtConfiguracion(IConfiguration configuration)
    {
        Issuer = configuration["Jwt:Issuer"]
            ?? throw new InvalidOperationException("Falta Jwt:Issuer.");

        Audience = configuration["Jwt:Audience"]
            ?? throw new InvalidOperationException("Falta Jwt:Audience.");

        var base64 = configuration["Jwt:SigningKeyBase64"]
            ?? throw new InvalidOperationException(
                "Falta Jwt:SigningKeyBase64.");

        byte[] key;

        try
        {
            key = Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                "La clave JWT debe estar codificada en Base64.", ex);
        }

        if (key.Length < 32)
            throw new InvalidOperationException(
                "La clave JWT debe contener al menos 32 bytes.");

        SigningKey = new SymmetricSecurityKey(key);
    }
}
