using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using NexoRuta.Application.Administracion.Results;

namespace NexoRuta.Api.Core.Security;

public sealed class JwtTokenService(JwtConfiguracion config, TimeProvider reloj)
{
    public (string Token, DateTime ExpiraEnUtc) Generar(
        SesionIniciada sesion, Guid? sesionMovilId = null, DateTimeOffset? finSesion = null)
    {
        var ahora = reloj.GetUtcNow().UtcDateTime;
        var expiracion = ahora.AddMinutes(15);
        if (finSesion is not null && finSesion.Value.UtcDateTime < expiracion)
            expiracion = finSesion.Value.UtcDateTime;

        var claims = new List<Claim>
        {
            new Claim(
                JwtRegisteredClaimNames.Sub,
                sesion.UsuarioId.ToString()),

            new Claim(
                JwtRegisteredClaimNames.Jti,
                Guid.NewGuid().ToString("N")),
            new Claim(
                JwtSesionClaims.VersionSesion,
                sesion.VersionSesion.ToString(CultureInfo.InvariantCulture)),
        };

        if (sesionMovilId is { } id)
            claims.Add(new Claim(JwtSesionClaims.SesionMovil, id.ToString()));
        if (sesion.AccesoId is Guid accessId)
            claims.Add(new Claim(AccesoSeleccionado.ClaimAccesoId, accessId.ToString()));
        if (sesion.SinOrganizacion)
            claims.Add(new Claim(AccesoSeleccionado.ClaimAmbito, "identidad"));
        if (sesion.EsPlataforma)
            claims.Add(new Claim(AccesoSeleccionado.ClaimAmbito, "plataforma"));
        if (sesion.OrganizacionSuspendida)
            claims.Add(new Claim(AccesoSeleccionado.ClaimAmbito, AccesoSeleccionado.ClaimSesionSuspendida));

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
