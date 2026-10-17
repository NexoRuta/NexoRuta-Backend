using NexoRuta.Application.Notificaciones.Dtos;
namespace NexoRuta.Application.Notificaciones.Repositories;
public interface INotificacionesRepartoRepository
{
    Task RegistrarInstalacionAsync(Guid operadorId, Guid accesoId, Guid sesionId, Guid instalacionId, string plataforma, string fid, bool permitida, CancellationToken ct);
    Task<IReadOnlyList<AvisoRepartoDatos>> ListarAsync(Guid operadorId, Guid accesoId, CancellationToken ct);
    Task<AvisoRepartoDatos?> ObtenerAsync(Guid operadorId, Guid accesoId, Guid id, string? registro, CancellationToken ct);
}
