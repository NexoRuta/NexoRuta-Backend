namespace NexoRuta.Application.Administracion;

public interface IOperadoresRepository
{
    Task<IReadOnlyList<OperadorDisponible>> ListarAsync(CancellationToken cancellationToken = default);
    Task<OperadorDisponible?> ObtenerAsync(Guid operadorId, CancellationToken cancellationToken = default);
}

public sealed record OperadorDisponible(Guid OperadorId, string Nombre);
