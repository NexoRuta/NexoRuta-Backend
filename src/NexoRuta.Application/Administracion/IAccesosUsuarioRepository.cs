using NexoRuta.Domain.Administracion;

namespace NexoRuta.Application.Administracion;

public interface IAccesosUsuarioRepository
{
    Task<IReadOnlyList<ContextoUsuario>> ListarAsync(TipoAccesoUsuario tipo, CancellationToken cancellationToken = default);
    Task<ContextoUsuario?> ObtenerAsync(Guid accesoId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OperadorDisponible>> ListarOperadoresAsync(CancellationToken cancellationToken = default);
    Task<OperadorDisponible?> ObtenerOperadorAsync(Guid operadorId, CancellationToken cancellationToken = default);
}

public sealed record OperadorDisponible(Guid OperadorId, string Nombre);
