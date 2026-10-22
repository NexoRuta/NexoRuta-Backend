
namespace NexoRuta.Application.Administracion.Repositories;

public interface IPermisosUsuarioRepository
{
    Task<bool> TienePermisoAsync(
        Guid usuarioId,
        Guid accesoId,
        string codigoPermiso,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListarCodigosAsync(
        Guid usuarioId,
        Guid accesoId,
        CancellationToken cancellationToken = default);
}
