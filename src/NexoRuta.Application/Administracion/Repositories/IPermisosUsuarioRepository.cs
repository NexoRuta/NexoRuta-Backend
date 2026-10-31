
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
    Task<bool> PuedeGestionarDecisionesOperativasEnvioAsync(
        Guid usuarioId,
        Guid accesoId,
        Guid rolId,
        Guid operadorId,
        CancellationToken cancellationToken = default);
}
