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
    UserManager<Usuario> users, ISesionesMovilesRepository sesiones, TimeProvider reloj, ILogger<JwtValidacionEventos> logger) : JwtBearerEvents
{
    private static void Fail(TokenValidatedContext context,string reason,string message)
    { context.HttpContext.Items[AuthDiagnostics.ReasonItem]=reason;context.Fail(message); }
    private static string FailureReason(Exception? error)=>error switch
    {
        Microsoft.IdentityModel.Tokens.SecurityTokenExpiredException => "token_expired",
        Microsoft.IdentityModel.Tokens.SecurityTokenNotYetValidException => "token_not_yet_valid",
        Microsoft.IdentityModel.Tokens.SecurityTokenSignatureKeyNotFoundException => "signature_key_unavailable",
        Microsoft.IdentityModel.Tokens.SecurityTokenInvalidSignatureException => "invalid_signature",
        Microsoft.IdentityModel.Tokens.SecurityTokenInvalidIssuerException => "invalid_issuer",
        Microsoft.IdentityModel.Tokens.SecurityTokenInvalidAudienceException => "invalid_audience",
        _ => "invalid_token"
    };
    public override Task AuthenticationFailed(AuthenticationFailedContext context)
    { context.HttpContext.Items[AuthDiagnostics.ReasonItem]=FailureReason(context.Exception);return Task.CompletedTask; }
    public override Task Challenge(JwtBearerChallengeContext context)
    {
        var reason=context.HttpContext.Items[AuthDiagnostics.ReasonItem] as string
            ?? (context.HttpContext.User.Identity?.IsAuthenticated==true ? "access_context_changed"
                : context.AuthenticateFailure is {} error ? FailureReason(error)
                : context.Request.Headers.Authorization.Count==0 ? "missing_bearer" : "invalid_bearer");
        AuthDiagnostics.Rejected(context.HttpContext,logger,401,reason);
        return Task.CompletedTask;
    }
    public override Task Forbidden(ForbiddenContext context)
    {AuthDiagnostics.Rejected(context.HttpContext,logger,403,"permission_denied");return Task.CompletedTask;}

    public override async Task TokenValidated(
        TokenValidatedContext context)
    {
        var usuarioClaim = context.Principal?
            .FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

        var accesoClaim = context.Principal?
            .FindFirst(AccesoSeleccionado.ClaimAccesoId)?.Value;

        if (!Guid.TryParse(usuarioClaim, out var usuarioId) || usuarioId == Guid.Empty)
        {
            Fail(context,"invalid_subject","Identidad inválida.");
            return;
        }

        var credentialVersionClaim = context.Principal?.FindFirst(JwtSesionClaims.VersionCredencial)?.Value;
        if (!long.TryParse(
                credentialVersionClaim,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var credentialVersion))
        {
            Fail(context,"invalid_credential_version","Credential session version is invalid.");
            return;
        }

        var account = await users.FindByIdAsync(usuarioId.ToString());
        if (account is null || account.VersionCredencial != credentialVersion)
        {
            Fail(context,"credential_revoked","The credential session is no longer available.");
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
            Fail(context,"invalid_access_version","Versión de sesión inválida.");
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
            Fail(context,"mobile_session_unavailable","La sesión móvil ya no está disponible.");
            return;
        }
        if (ambito == "identidad")
        {
            var unassignedIdentity = accesoClaim is null && versionToken == 0
                ? await identidades.ObtenerSinOrganizacionAsync(usuarioId, context.HttpContext.RequestAborted)
                : null;
            if (unassignedIdentity is null)
                Fail(context,"identity_context_changed","La sesión sin organización ya no está disponible. Ingresá nuevamente.");
            else
                context.HttpContext.Items[typeof(IdentidadSinOrganizacion)] = unassignedIdentity;
            return;
        }
        if (!Guid.TryParse(accesoClaim, out var accesoId))
        {
            Fail(context,"invalid_access","Acceso inválido.");
            return;
        }
        if (ambito == "plataforma")
        {
            var platformAccess = await plataforma.ObtenerAccesoAsync(accesoId, context.HttpContext.RequestAborted);
            if (platformAccess is null || platformAccess.UsuarioId != usuarioId || platformAccess.VersionSesion != versionToken)
            {
                Fail(context,"platform_context_changed","The platform session is no longer available.");
                return;
            }
            context.HttpContext.Items[typeof(ContextoPlataforma)] = platformAccess;
            return;
        }
        if (ambito is not null && ambito != AccesoSeleccionado.ClaimSesionSuspendida)
        {
            Fail(context,"invalid_scope","Invalid authentication scope.");
            return;
        }

        var acceso = await accesos.ObtenerAsync(
            accesoId,
            context.HttpContext.RequestAborted);


        if (acceso is null ||
            acceso.UsuarioId != usuarioId ||
            acceso.VersionSesion != versionToken)
        {
            Fail(context,"access_context_changed","La sesión ya no está disponible.");
            return;
        }

        if (ambito == AccesoSeleccionado.ClaimSesionSuspendida)
        {
            if (acceso.Estado != NexoRuta.Domain.Administracion.EstadoOrganizacion.Suspendida)
            {
                Fail(context,"suspension_context_changed","La organización ya no está suspendida. Ingresá nuevamente.");
                return;
            }
            var organizationId = acceso.OperadorId ?? acceso.ComercioId;
            if (organizationId is null)
            {
                Fail(context,"invalid_organization_context","Restricted organization context is invalid.");
                return;
            }
            context.HttpContext.Items[typeof(ContextoOrganizacionSuspendida)] = new ContextoOrganizacionSuspendida(
                acceso.UsuarioId, acceso.AccesoId, organizationId.Value, acceso.TipoAcceso,
                acceso.OperadorId.HasValue ? acceso.OperadorNombre! : acceso.ComercioNombre!, acceso.VersionSesion);
            return;
        }

        if (acceso.Estado == NexoRuta.Domain.Administracion.EstadoOrganizacion.Suspendida)
        {
            Fail(context,"organization_suspended","La organización está suspendida.");
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
