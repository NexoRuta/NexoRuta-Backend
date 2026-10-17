using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Repositories;

namespace NexoRuta.Application.Administracion.UseCases;

public sealed class EsPropietarioOrganizacionUseCase(
    IUsuarioActual usuarioActual,
    IAccesosUsuarioRepository accesos)
{
    public async Task<bool> EjecutarAsync(CancellationToken cancellationToken = default)
    {
        var actor = await usuarioActual.ObtenerAsync(cancellationToken);
        return await accesos.EsPropietarioAsync(actor.UsuarioId, actor.AccesoId, cancellationToken);
    }
}
