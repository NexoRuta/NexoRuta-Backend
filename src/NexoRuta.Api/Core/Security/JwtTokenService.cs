using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using NexoRuta.Application.Administracion.Results;

namespace NexoRuta.Api.Core.Security;

public sealed class JwtTokenService(JwtConfiguracion config)
{
    public (string Token, DateTime ExpiraEnUtc) Generar(
        SesionIniciada sesion)
    {
        var ahora = DateTime.UtcNow;
        var expiracion = ahora.AddMinutes(15);

        var claims = new[]
        {
            new Claim(
                JwtRegisteredClaimNames.Sub,
                sesion.UsuarioId.ToString()),

            new Claim(
                AccesoSeleccionado.ClaimAccesoId,
                sesion.AccesoId.ToString()),

            new Claim(
                JwtRegisteredClaimNames.Jti,
                Guid.NewGuid().ToString("N")),
            new Claim(
                JwtSesionClaims.VersionSesion,
                sesion.VersionSesion.ToString(CultureInfo.InvariantCulture)),
        };

        var token = new JwtSecurityToken(
            issuer: config.Issuer,
            audience: config.Audience,
            claims: claims,
            notBefore: ahora,
            expires: expiracion,
            signingCredentials: new SigningCredentials(
                config.SigningKey,
                SecurityAlgorithms.HmacSha256));

        return (
            new JwtSecurityTokenHandler().WriteToken(token),
            expiracion);
    }
}
