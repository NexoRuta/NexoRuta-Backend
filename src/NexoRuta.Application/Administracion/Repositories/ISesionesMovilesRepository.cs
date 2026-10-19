using NexoRuta.Domain.Administracion;

namespace NexoRuta.Application.Administracion.Repositories;

public interface ISesionesMovilesRepository
{
    Task CrearAsync(SesionMovil sesion, string tokenHash, CancellationToken ct);
    Task<SesionMovil?> ObtenerPorTokenAsync(string tokenHash, CancellationToken ct);
    Task<SesionMovil?> RotarAsync(string tokenHash, string nuevoHash, DateTimeOffset ahora, CancellationToken ct);
    Task RevocarAsync(string tokenHash, DateTimeOffset ahora, CancellationToken ct);
    Task<bool> EstaVigenteAsync(Guid sesionId, Guid usuarioId, Guid accesoId, long version,
        DateTimeOffset ahora, CancellationToken ct);
}
