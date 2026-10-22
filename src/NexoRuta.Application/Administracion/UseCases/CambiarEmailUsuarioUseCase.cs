using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Repositories;

namespace NexoRuta.Application.Administracion.UseCases;

public sealed class CambiarEmailUsuarioUseCase(
    IUsuarioActual usuarioActual,
    IPermisosUsuarioRepository permisos,
    IUsuariosOrganizacionRepository usuarios)
{
    public async Task<bool> EjecutarAsync(Guid usuarioId, string email, long version, CancellationToken ct = default)
    {
        if (usuarioId == Guid.Empty) throw new ArgumentException("El usuario es obligatorio.", nameof(usuarioId));
        if (version < 0) throw new ArgumentException("La versión no es válida.", nameof(version));
        var normalizado = ValidacionCredenciales.NormalizarEmail(email);
        var actor = await usuarioActual.ObtenerAsync(ct);
        if (usuarioId == actor.UsuarioId ||
            !await permisos.TienePermisoAsync(actor.UsuarioId, actor.AccesoId, "usuarios.gestionar", ct))
            throw new AccesoNoPermitidoException();
        return await usuarios.CambiarEmailAsync(actor, usuarioId, normalizado, version, ct);
    }
}
