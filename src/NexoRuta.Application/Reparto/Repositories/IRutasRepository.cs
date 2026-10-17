using NexoRuta.Application.Reparto.Dtos;

namespace NexoRuta.Application.Reparto.Repositories;

public interface IRutasRepository
{
    Task<IReadOnlyList<HojaRutaDatos>> ListarAsync(Guid operadorId, Guid? repartidorId, DateOnly fecha, CancellationToken ct);
    Task<HojaRutaDatos> PlanificarAsync(Guid operadorId, PlanificarRutaDatos datos, CancellationToken ct);
    Task<HojaRutaDatos?> DespacharAsync(Guid operadorId, Guid rutaId, long version, CancellationToken ct);
    Task<bool> RecibirAsync(Guid operadorId, Guid envioId, long version, CancellationToken ct);
    Task<bool> ConfirmarDestinoAsync(Guid comercioId, Guid usuarioId, Guid envioId, long version,
        double latitud, double longitud, CancellationToken ct);
}
