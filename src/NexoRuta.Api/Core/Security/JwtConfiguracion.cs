
using Microsoft.IdentityModel.Tokens;

namespace NexoRuta.Api.Core.Security;

public sealed class JwtConfiguracion
{
    public string Issuer { get; }
    public string Audience { get; }
    public SymmetricSecurityKey SigningKey { get; }
    public int VigenciaWebMinutos { get; }

    public JwtConfiguracion(IConfiguration configuration)
    {
        Issuer = configuration["Jwt:Issuer"]
            ?? throw new InvalidOperationException("Falta Jwt:Issuer.");

        Audience = configuration["Jwt:Audience"]
            ?? throw new InvalidOperationException("Falta Jwt:Audience.");
        VigenciaWebMinutos = configuration.GetValue("Jwt:VigenciaWebMinutos", 15);
        if (VigenciaWebMinutos is < 1 or > 240)
            throw new InvalidOperationException("Jwt:VigenciaWebMinutos debe estar entre 1 y 240.");


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
