using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Application.Reparto.Dtos;
using NexoRuta.Application.Reparto.Exceptions;
using NexoRuta.Application.Reparto.Repositories;

namespace NexoRuta.Application.Reparto.UseCases;

public sealed class GestionarRutasUseCase(IUsuarioActual usuario, IRutasRepository rutas, IRepartidoresRepository repartidores,
    IPermisosUsuarioRepository permisos)
{
    public async Task<IReadOnlyList<HojaRutaDatos>> ListarAsync(DateOnly fecha, bool propia, CancellationToken ct)
    {
        var actor = await usuario.ObtenerAsync(ct);
        var op = actor.OperadorId ?? throw new RepartoNoPermitidoException();
        if (propia && await repartidores.ObtenerActivoAsync(op, actor.AccesoId, ct) is null) throw new RepartoNoPermitidoException();
        return await rutas.ListarAsync(op, propia ? actor.AccesoId : null, fecha, ct);
    }
    public async Task<HojaRutaDatos> PlanificarAsync(PlanificarRutaDatos datos, CancellationToken ct)
    {
        var actor = await usuario.ObtenerAsync(ct);
        return await rutas.PlanificarAsync(actor.OperadorId ?? throw new RepartoNoPermitidoException(), datos, ct);
    }
    public async Task<HojaRutaDatos?> DespacharAsync(Guid id, long version, CancellationToken ct)
    {
        var actor = await usuario.ObtenerAsync(ct);
        return await rutas.DespacharAsync(actor.OperadorId ?? throw new RepartoNoPermitidoException(), id, version, ct);
    }
    public async Task<HojaRutaDatos?> CancelarAsync(Guid id, long version, CancellationToken ct)
    {
        var actor = await usuario.ObtenerAsync(ct);
        return await rutas.CancelarAsync(actor.OperadorId ?? throw new RepartoNoPermitidoException(), id, version, ct);
    }
    public async Task<HojaRutaDatos?> ObtenerPropiaAsync(Guid id, CancellationToken ct)
    {
        var actor = await usuario.ObtenerAsync(ct);
        var op = actor.OperadorId ?? throw new RepartoNoPermitidoException();
        if (await repartidores.ObtenerActivoAsync(op, actor.AccesoId, ct) is null) throw new RepartoNoPermitidoException();
        return await rutas.ObtenerPropiaAsync(op, actor.AccesoId, id, ct);
    }
    public async Task<HojaRutaDatos?> ReasignarAsync(Guid envioId, ReasignarEnvioDatos datos, CancellationToken ct)
    {
        var actor = await usuario.ObtenerAsync(ct);
        return await rutas.ReasignarAsync(actor.OperadorId ?? throw new RepartoNoPermitidoException(), envioId, datos, ct);
    }
    public async Task<ReprogramacionEnvioDatos?> ReprogramarAsync(Guid envioId,
        SolicitarReprogramacionEnvioDatos datos, CancellationToken ct)
    {
        var actor = await usuario.ObtenerAsync(ct);
        return await rutas.ReprogramarAsync(actor.OperadorId ?? throw new RepartoNoPermitidoException(), envioId, datos, ct);
    }
    public async Task<DecisionDevolucionEnvioDatos?> IniciarDevolucionAsync(Guid envioId,
        IniciarDevolucionEnvioDatos datos, CancellationToken ct)
    {
        var actor = await ActorConGestionOperativaAsync(ct);
        return await rutas.IniciarDevolucionAsync(actor, envioId, datos, ct);
    }
    public async Task<ConfirmacionExtravioEnvioDatos?> ConfirmarExtravioAsync(Guid envioId,
        ConfirmarExtravioEnvioDatos datos, CancellationToken ct)
    {
        var actor = await ActorConGestionOperativaAsync(ct);
        return await rutas.ConfirmarExtravioAsync(actor, envioId, datos, ct);
    }
    public async Task<bool> PuedeGestionarDecisionesOperativasAsync(CancellationToken ct)
    {
        var actor = await usuario.ObtenerAsync(ct);
        return actor.TipoAcceso == NexoRuta.Domain.Administracion.TipoAccesoUsuario.Operador
            && actor.OperadorId is { } operadorId && operadorId != Guid.Empty
            && actor.ComercioId is null && actor.RolId != Guid.Empty && actor.UsuarioId != Guid.Empty
            && actor.AccesoId != Guid.Empty
            && await permisos.PuedeGestionarDecisionesOperativasEnvioAsync(actor.UsuarioId, actor.AccesoId,
                actor.RolId, operadorId, ct);
    }
    private async Task<NexoRuta.Application.Administracion.Context.ContextoUsuario> ActorConGestionOperativaAsync(CancellationToken ct)
    {
        var actor = await usuario.ObtenerAsync(ct);
        if (actor.TipoAcceso != NexoRuta.Domain.Administracion.TipoAccesoUsuario.Operador
            || actor.OperadorId is not { } operadorId || operadorId == Guid.Empty
            || actor.ComercioId is not null || actor.RolId == Guid.Empty
            || actor.UsuarioId == Guid.Empty || actor.AccesoId == Guid.Empty
            || !await permisos.PuedeGestionarDecisionesOperativasEnvioAsync(actor.UsuarioId, actor.AccesoId,
                actor.RolId, operadorId, ct))
            throw new RepartoNoPermitidoException();
        return actor;
    }
    public async Task<bool> RecibirAsync(Guid envioId, long version, CancellationToken ct)
    {
        var actor = await usuario.ObtenerAsync(ct);
        return await rutas.RecibirAsync(actor.OperadorId ?? throw new RepartoNoPermitidoException(), envioId, version, ct);
    }
}
