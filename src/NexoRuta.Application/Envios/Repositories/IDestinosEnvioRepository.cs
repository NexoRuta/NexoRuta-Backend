using NexoRuta.Application.Envios.Dtos;

namespace NexoRuta.Application.Envios.Repositories;

public interface IDestinosEnvioRepository
{
    Task<DestinoEnvioDatos?> ObtenerAsync(Guid comercioId, Guid envioId, CancellationToken ct);
    Task<bool> ConfirmarAsync(Guid comercioId, Guid usuarioId, Guid envioId, long version, double latitud, double longitud, CancellationToken ct);
}
