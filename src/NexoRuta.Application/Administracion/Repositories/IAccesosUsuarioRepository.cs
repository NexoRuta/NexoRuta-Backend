using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Dtos;
using NexoRuta.Domain.Administracion;

namespace NexoRuta.Application.Administracion.Repositories;

public interface IAccesosUsuarioRepository // => EfAccesosUsuarioRepository
{
    Task<IReadOnlyList<ContextoUsuario>> ListarAsync(TipoAccesoUsuario tipo, CancellationToken cancellationToken = default);
    Task<ContextoUsuario?> ObtenerAsync(Guid accesoId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OperadorDisponible>> ListarOperadoresAsync(CancellationToken cancellationToken = default);
    Task<OperadorDisponible?> ObtenerOperadorAsync(Guid operadorId, CancellationToken cancellationToken = default);
}
