
namespace NexoRuta.Application.Administracion.Repositories;

public interface IPermisosUsuarioRepository
{
    Task<bool> TienePermisoAsync(
        Guid usuarioId,
        Guid accesoId,
        string codigoPermiso,
        CancellationToken cancellationToken = default);
}
