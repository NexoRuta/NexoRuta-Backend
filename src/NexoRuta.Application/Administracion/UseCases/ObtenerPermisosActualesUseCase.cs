using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Repositories;

namespace NexoRuta.Application.Administracion.UseCases;

public sealed class ObtenerPermisosActualesUseCase(
    IUsuarioActual usuarioActual,
    IPermisosUsuarioRepository permisos)
{
    public async Task<IReadOnlyList<string>> EjecutarAsync(CancellationToken ct = default)
    {
        var actor = await usuarioActual.ObtenerAsync(ct);
        return await permisos.ListarCodigosAsync(actor.UsuarioId, actor.AccesoId, ct);
    }
}
