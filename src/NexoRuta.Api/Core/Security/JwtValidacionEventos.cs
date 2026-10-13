
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Repositories;

namespace NexoRuta.Api.Core.Security;

public sealed class JwtValidacionEventos(
    IAccesosUsuarioRepository accesos) : JwtBearerEvents
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

        var acceso = await accesos.ObtenerAsync(
            accesoId,
            context.HttpContext.RequestAborted);

        if (acceso is null || acceso.UsuarioId != usuarioId)
        {
            context.Fail("El acceso ya no está disponible.");
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
