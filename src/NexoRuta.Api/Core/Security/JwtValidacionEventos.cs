using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Domain.Administracion;
namespace NexoRuta.Api.Core.Security;

public sealed class JwtValidacionEventos(
    IAccesosUsuarioRepository accesos, IPlataformaRepository plataforma, IRegistroRepository identidades,
    UserManager<Usuario> users) : JwtBearerEvents
{
    public override async Task TokenValidated(
        TokenValidatedContext context)
    {
        var usuarioClaim = context.Principal?
            .FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

        var accesoClaim = context.Principal?
            .FindFirst(AccesoSeleccionado.ClaimAccesoId)?.Value;

        if (!Guid.TryParse(usuarioClaim, out var usuarioId) || usuarioId == Guid.Empty)
        {
            context.Fail("Identidad inválida.");
            return;
        }

        var credentialVersionClaim = context.Principal?.FindFirst(JwtSesionClaims.VersionCredencial)?.Value;
        var credentialVersion = 0L; // Existing JWTs predate the global credential-version claim.
        if (credentialVersionClaim is not null && !long.TryParse(
                credentialVersionClaim,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out credentialVersion))
        {
            context.Fail("Credential session version is invalid.");
            return;
        }

        var account = await users.FindByIdAsync(usuarioId.ToString());
        if (account is null || account.VersionCredencial != credentialVersion)
        {
            context.Fail("The credential session is no longer available.");
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
        var claimSesion = context.Principal?.FindAll(JwtSesionClaims.SesionMovil).ToArray() ?? [];
        if (claimSesion.Length > 0 && (claimSesion.Length != 1 || ambito is not null
            || !Guid.TryParse(claimSesion[0].Value, out var sesionId)
            || !Guid.TryParse(accesoClaim, out var accesoMovilId)
            || !await sesiones.EstaVigenteAsync(sesionId, usuarioId, accesoMovilId, versionToken,
                reloj.GetUtcNow(), context.HttpContext.RequestAborted)))
        {
            context.Fail("La sesión móvil ya no está disponible.");
            return;
        }
        if (ambito == "identidad")
        {
            var unassignedIdentity = accesoClaim is null && versionToken == 0
                ? await identidades.ObtenerSinOrganizacionAsync(usuarioId, context.HttpContext.RequestAborted)
                : null;
            if (unassignedIdentity is null)
                context.Fail("La sesión sin organización ya no está disponible. Ingresá nuevamente.");
            else
                context.HttpContext.Items[typeof(IdentidadSinOrganizacion)] = unassignedIdentity;
            return;
        }
        if (!Guid.TryParse(accesoClaim, out var accesoId))
        {
            context.Fail("Acceso inválido.");
            return;
        }
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
        if (ambito is not null && ambito != AccesoSeleccionado.ClaimSesionSuspendida)
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

        if (ambito == AccesoSeleccionado.ClaimSesionSuspendida)
        {
            if (acceso.Estado != NexoRuta.Domain.Administracion.EstadoOrganizacion.Suspendida)
            {
                context.Fail("La organización ya no está suspendida. Ingresá nuevamente.");
                return;
            }
            var organizationId = acceso.OperadorId ?? acceso.ComercioId;
            if (organizationId is null)
            {
                context.Fail("Restricted organization context is invalid.");
                return;
            }
            context.HttpContext.Items[typeof(ContextoOrganizacionSuspendida)] = new ContextoOrganizacionSuspendida(
                acceso.UsuarioId, acceso.AccesoId, organizationId.Value, acceso.TipoAcceso,
                acceso.OperadorId.HasValue ? acceso.OperadorNombre! : acceso.ComercioNombre!, acceso.VersionSesion);
            return;
        }

        if (acceso.Estado == NexoRuta.Domain.Administracion.EstadoOrganizacion.Suspendida)
        {
            context.Fail("La organización está suspendida.");
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
