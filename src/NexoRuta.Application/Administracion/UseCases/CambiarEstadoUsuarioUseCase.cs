
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Repositories;

namespace NexoRuta.Application.Administracion.UseCases;

public sealed class CambiarEstadoUsuarioUseCase(
    IUsuarioActual usuarioActual,
    IPermisosUsuarioRepository permisos,
    IUsuariosOrganizacionRepository usuarios)
{
    public async Task<bool> EjecutarAsync(
        Guid usuarioId,
        bool activo,
        CancellationToken ct = default)
    {
        if (usuarioId == Guid.Empty)
        {
            throw new ArgumentException(
                "El usuario es obligatorio.",
                nameof(usuarioId));
        }

        var actor = await usuarioActual.ObtenerAsync(ct);

        if (!await permisos.TienePermisoAsync(
            actor.UsuarioId,
            actor.AccesoId,
            "usuarios.gestionar",
            ct))
        {
            throw new AccesoNoPermitidoException();
        }

        // Own-identity access and privilege changes are forbidden, including idempotent state requests.
        if (usuarioId == actor.UsuarioId)
        {
            throw new AccesoNoPermitidoException();
        }

        return await usuarios.CambiarEstadoAsync(
            actor,
            usuarioId,
            activo,
            ct);
    }
}
