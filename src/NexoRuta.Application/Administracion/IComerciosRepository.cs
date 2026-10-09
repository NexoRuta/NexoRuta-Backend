namespace NexoRuta.Application.Administracion;

public interface IComerciosRepository
{
    Task<IReadOnlyList<ComercioResumen>> ListarAsync(CancellationToken cancellationToken = default);
    Task<ComercioResumen?> ObtenerAsync(Guid comercioId, CancellationToken cancellationToken = default);
}

public sealed record ComercioResumen(Guid ComercioId, string Nombre);
