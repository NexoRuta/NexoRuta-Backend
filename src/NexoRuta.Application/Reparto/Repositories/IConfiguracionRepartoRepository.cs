using NexoRuta.Application.Reparto.Dtos;

namespace NexoRuta.Application.Reparto.Repositories;

public interface IConfiguracionRepartoRepository
{
    Task<ConfiguracionRepartoDatos?> ObtenerAsync(Guid operadorId, CancellationToken ct);
    Task<ConfiguracionRepartoDatos> GuardarAsync(Guid operadorId, ConfiguracionRepartoDatos datos, CancellationToken ct);
    Task<IReadOnlyList<VehiculoDatos>> VehiculosAsync(Guid operadorId, CancellationToken ct);
    Task<VehiculoDatos?> GuardarVehiculoAsync(Guid operadorId, VehiculoDatos datos, CancellationToken ct);
}
