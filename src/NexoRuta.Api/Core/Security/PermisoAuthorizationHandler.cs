
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Domain.Administracion;

namespace NexoRuta.Api.Core.Security;

public sealed class PermisoAuthorizationHandler(
    IPermisosUsuarioRepository permisos,
    IHttpContextAccessor httpContextAccessor)
    : AuthorizationHandler<PermisoRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermisoRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
            return;

        var httpContext = httpContextAccessor.HttpContext;

        if (httpContext?.Items[typeof(ContextoUsuario)]
            is not ContextoUsuario usuario)
            return;

        if (!CatalogoPermisos.EsAplicable(requirement.Codigo, usuario.TipoAcceso))
            return;

        var claimUsuarioId =
            context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

        var claimAccesoId = context.User.FindFirst(
            AccesoSeleccionado.ClaimAccesoId)?.Value;

        if (!Guid.TryParse(claimUsuarioId, out var usuarioId) ||
            !Guid.TryParse(claimAccesoId, out var accesoId) ||
            usuarioId != usuario.UsuarioId ||
            accesoId != usuario.AccesoId)
        {
            return;
        }

        var autorizado = await permisos.TienePermisoAsync(
            usuarioId,
            accesoId,
            requirement.Codigo,
            httpContext.RequestAborted);

        if (autorizado)
            context.Succeed(requirement);
    }
}
