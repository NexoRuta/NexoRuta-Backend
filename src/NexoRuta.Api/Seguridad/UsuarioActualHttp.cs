using System.Security.Claims;
using NexoRuta.Application.Administracion;

namespace NexoRuta.Api.Seguridad;

public sealed class UsuarioActualHttp(IHttpContextAccessor httpContext) : IUsuarioActual
{
    public Task<ContextoUsuario> ObtenerAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var contexto = httpContext.HttpContext;
        if (contexto is null
            || !Guid.TryParse(contexto.User.FindFirstValue(AccesoSeleccionado.ClaimAccesoId), out var accesoId)
            || contexto.Items[typeof(ContextoUsuario)] is not ContextoUsuario usuario
            || usuario.AccesoId != accesoId)
            throw new AccesoActualNoDisponibleException();

        return Task.FromResult(usuario);
    }
}
