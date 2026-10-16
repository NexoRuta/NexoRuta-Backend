using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Repositories;
namespace NexoRuta.Api.Core.Security;

public sealed class JwtValidacionEventos(
    IAccesosUsuarioRepository accesos, IPlataformaRepository plataforma) : JwtBearerEvents
{
    public override async Task TokenValidated(
        TokenValidatedContext context)
    {
        var usuarioClaim = context.Principal?
            .FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

        var accesoClaim = context.Principal?
            .FindFirst(AccesoSeleccionado.ClaimAccesoId)?.Value;

        if (!Guid.TryParse(usuarioClaim, out var usuarioId) ||
            !Guid.TryParse(accesoClaim, out var accesoId))
        {
            context.Fail("Identidad inválida.");
            return;
        }

        var versionClaim = context.Principal?
            .FindFirst(JwtSesionClaims.VersionSesion)?.Value;

        if (!long.TryParse(
                versionClaim,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var versionToken))
        {
            context.Fail("Versión de sesión inválida.");
            return;
        }

        var ambito = context.Principal?.FindFirst(AccesoSeleccionado.ClaimAmbito)?.Value;
        if (ambito == "plataforma")
        {
            var platformAccess = await plataforma.ObtenerAccesoAsync(accesoId, context.HttpContext.RequestAborted);
            if (platformAccess is null || platformAccess.UsuarioId != usuarioId || platformAccess.VersionSesion != versionToken)
            {
                context.Fail("The platform session is no longer available.");
                return;
            }
            context.HttpContext.Items[typeof(ContextoPlataforma)] = platformAccess;
            return;
        }
        if (ambito is not null)
        {
            context.Fail("Invalid authentication scope.");
            return;
        }

        var acceso = await accesos.ObtenerAsync(
            accesoId,
            context.HttpContext.RequestAborted);


        if (acceso is null ||
            acceso.UsuarioId != usuarioId ||
            acceso.VersionSesion != versionToken)
        {
            context.Fail("La sesión ya no está disponible.");
            return;
        }


        context.HttpContext.Items[typeof(ContextoUsuario)] = acceso;

        if (context.Principal?.Identity is ClaimsIdentity identity)
        {
            identity.AddClaim(new Claim(
                AccesoSeleccionado.ClaimTipoAcceso,
                acceso.Tipo));
        }
    }
}
