
using System.Security.Claims;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Interfaces;

namespace NexoRuta.Api.Core.Security;

public sealed class UsuarioActualHttp(
    IHttpContextAccessor httpContext
) : IUsuarioActual
{
    public Task<ContextoUsuario> ObtenerAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var contexto = httpContext.HttpContext
            ?? throw new AccesoActualNoDisponibleException();

        var usuario = contexto.Items[typeof(ContextoUsuario)]
            as ContextoUsuario
            ?? throw new AccesoActualNoDisponibleException();

        var claimAccesoId = contexto.User.FindFirstValue(
            AccesoSeleccionado.ClaimAccesoId);

        if (!Guid.TryParse(claimAccesoId, out var accesoId)
            || usuario.AccesoId != accesoId)
        {
            throw new AccesoActualNoDisponibleException();
        }

        return Task.FromResult(usuario);
    }
}
