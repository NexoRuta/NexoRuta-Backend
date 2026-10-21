using NexoRuta.Application.Reparto.Dtos;

namespace NexoRuta.Application.Reparto.Repositories;

public interface IRutasRepository
{
    Task<IReadOnlyList<HojaRutaDatos>> ListarAsync(Guid operadorId, Guid? repartidorId, DateOnly fecha, CancellationToken ct);
    Task<HojaRutaDatos?> ObtenerPropiaAsync(Guid operadorId, Guid repartidorId, Guid rutaId, CancellationToken ct);
    Task<HojaRutaDatos?> ReasignarAsync(Guid operadorId, Guid envioId, ReasignarEnvioDatos datos, CancellationToken ct);
    Task<HojaRutaDatos> PlanificarAsync(Guid operadorId, PlanificarRutaDatos datos, CancellationToken ct);
    Task<HojaRutaDatos?> DespacharAsync(Guid operadorId, Guid rutaId, long version, CancellationToken ct);
    Task<bool> RecibirAsync(Guid operadorId, Guid envioId, long version, CancellationToken ct);

}
