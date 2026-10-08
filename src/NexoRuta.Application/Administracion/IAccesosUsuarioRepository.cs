using NexoRuta.Domain.Administracion;

namespace NexoRuta.Application.Administracion;

public interface IAccesosUsuarioRepository
{
    Task<IReadOnlyList<ContextoUsuario>> ListarAsync(TipoAccesoUsuario tipo, CancellationToken cancellationToken = default);
    Task<ContextoUsuario?> ObtenerAsync(Guid accesoId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OperadorDisponible>> ListarOperadoresAsync(Guid comercioId, CancellationToken cancellationToken = default);
    Task<OperadorDisponible?> ObtenerOperadorAsync(Guid comercioId, Guid operadorId, CancellationToken cancellationToken = default);
}

public sealed record OperadorDisponible(Guid OperadorId, Guid OperadorComercioId, string Nombre);
