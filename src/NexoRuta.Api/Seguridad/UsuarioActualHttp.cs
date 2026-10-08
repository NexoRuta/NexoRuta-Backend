using System.Security.Claims;
using NexoRuta.Application.Administracion;

namespace NexoRuta.Api.Seguridad;

public sealed class UsuarioActualHttp(IHttpContextAccessor httpContext, IAccesosUsuarioRepository accesos) : IUsuarioActual
{
    public async Task<ContextoUsuario> ObtenerAsync(CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(httpContext.HttpContext?.User.FindFirstValue(AccesoSeleccionado.ClaimAccesoId), out var accesoId))
            throw new AccesoActualNoDisponibleException();

        return await accesos.ObtenerAsync(accesoId, cancellationToken) ?? throw new AccesoActualNoDisponibleException();
    }
}
