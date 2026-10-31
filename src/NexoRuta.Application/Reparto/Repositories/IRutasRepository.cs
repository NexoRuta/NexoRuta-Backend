using NexoRuta.Application.Reparto.Dtos;
using NexoRuta.Application.Administracion.Context;

namespace NexoRuta.Application.Reparto.Repositories;

public interface IRutasRepository
{
    Task<IReadOnlyList<HojaRutaDatos>> ListarAsync(Guid operadorId, Guid? repartidorId, DateOnly fecha, CancellationToken ct);
    Task<HojaRutaDatos?> ObtenerPropiaAsync(Guid operadorId, Guid repartidorId, Guid rutaId, CancellationToken ct);
    Task<HojaRutaDatos?> ReasignarAsync(Guid operadorId, Guid envioId, ReasignarEnvioDatos datos, CancellationToken ct);
    Task<ReprogramacionEnvioDatos?> ReprogramarAsync(Guid operadorId, Guid envioId,
        SolicitarReprogramacionEnvioDatos datos, CancellationToken ct);
    Task<DecisionDevolucionEnvioDatos?> IniciarDevolucionAsync(ContextoUsuario actor, Guid envioId,
        IniciarDevolucionEnvioDatos datos, CancellationToken ct);
    Task<ConfirmacionExtravioEnvioDatos?> ConfirmarExtravioAsync(ContextoUsuario actor, Guid envioId,
        ConfirmarExtravioEnvioDatos datos, CancellationToken ct);
    Task<HojaRutaDatos> PlanificarAsync(Guid operadorId, PlanificarRutaDatos datos, CancellationToken ct);
    Task<HojaRutaDatos?> DespacharAsync(Guid operadorId, Guid rutaId, long version, CancellationToken ct);
    Task<HojaRutaDatos?> CancelarAsync(Guid operadorId, Guid rutaId, long version, CancellationToken ct);
    Task<bool> RecibirAsync(Guid operadorId, Guid envioId, long version, CancellationToken ct);

}
