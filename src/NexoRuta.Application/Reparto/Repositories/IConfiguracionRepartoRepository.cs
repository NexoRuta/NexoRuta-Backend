using NexoRuta.Application.Reparto.Dtos;

namespace NexoRuta.Application.Reparto.Repositories;

public interface IConfiguracionRepartoRepository
{
    Task<BaseOperativaDatos?> BaseAsync(Guid operadorId, CancellationToken ct);
    Task<BaseOperativaDatos> GuardarBaseAsync(Guid operadorId, GuardarBaseOperativaDatos datos, CancellationToken ct);
    Task<BaseOperativaDatos?> ConfirmarBaseAsync(NexoRuta.Application.Administracion.Context.ContextoUsuario actor, long version, double latitud, double longitud, DateTimeOffset ahora, CancellationToken ct);
    Task<ConfiguracionRepartoDatos?> ObtenerAsync(Guid operadorId, CancellationToken ct);
    Task<ConfiguracionRepartoDatos> GuardarAsync(Guid operadorId, ConfiguracionRepartoDatos datos, CancellationToken ct);
    Task<IReadOnlyList<VehiculoDatos>> VehiculosAsync(Guid operadorId, CancellationToken ct);
    Task<VehiculoDatos?> GuardarVehiculoAsync(Guid operadorId, VehiculoDatos datos, CancellationToken ct);
}
