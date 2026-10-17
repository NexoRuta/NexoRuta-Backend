using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Dtos;
using NexoRuta.Domain.Administracion;

namespace NexoRuta.Application.Administracion.Repositories;

public interface IAccesosUsuarioRepository // => EfAccesosUsuarioRepository
{
    Task<IReadOnlyList<ContextoUsuario>> ListarAsync(TipoAccesoUsuario tipo, CancellationToken cancellationToken = default);
    Task<ContextoUsuario?> ObtenerAsync(Guid accesoId, CancellationToken cancellationToken = default);
    Task<bool> EsPropietarioAsync(Guid usuarioId, Guid accesoId, CancellationToken cancellationToken = default);
}
