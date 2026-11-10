using NexoRuta.Application.Reparto.Dtos;
using NexoRuta.Application.Administracion.Context;

namespace NexoRuta.Application.Reparto.Repositories;

public interface IRutasRepository
{
    Task<bool> EntregarComercioAsync(Guid operadorId, Guid envioId, long version, CancellationToken ct);
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
    Task<HojaRutaDatos?> DespacharAsync(ContextoUsuario actor, Guid rutaId, long version, CancellationToken ct);
    Task<IReadOnlyList<VehiculoFlotaDatos>> FlotaAsync(Guid operadorId, CancellationToken ct);
    Task<ResultadoFlotaDatos?> ConfirmarFlotaAsync(ContextoUsuario actor, Guid vehiculoId,
        string accion, ComandoFlotaDatos datos, CancellationToken ct);
    Task<HojaRutaDatos?> CancelarAsync(Guid operadorId, Guid rutaId, long version, CancellationToken ct);
    Task<ConsultaRecepcionDatos?> ConsultarRecepcionAsync(Guid op, string codigo, CancellationToken ct);
    Task<ResultadoRecepcionDatos?> CotejarBultoAsync(ContextoUsuario actor, Guid id, CotejoBultoDatos datos, CancellationToken ct);
    Task<bool> RecibirAsync(Guid operadorId, Guid envioId, long version, CancellationToken ct);

    Task<IReadOnlyList<PerfilVialDatos>> PerfilesVialesAsync(Guid op,CancellationToken ct);
    Task<PerfilVialDatos?> ConfigurarPerfilAsync(ContextoUsuario actor,Guid vehiculo,ConfigurarPerfilVialDatos datos,CancellationToken ct);
    Task<EstimacionRutaDatos?> EstimarAsync(ContextoUsuario actor,Guid ruta,CalcularEstimacionDatos datos,CancellationToken ct);
    Task<IReadOnlyList<EstimacionRutaDatos>?> EstimacionesAsync(Guid op,Guid ruta,long? antes,CancellationToken ct);
}
