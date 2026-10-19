using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Reparto.Dtos;
using NexoRuta.Application.Reparto.Exceptions;
using NexoRuta.Application.Reparto.Repositories;

namespace NexoRuta.Application.Reparto.UseCases;

public sealed class GestionarRutasUseCase(IUsuarioActual usuario, IRutasRepository rutas, IRepartidoresRepository repartidores)
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
    public async Task<bool> RecibirAsync(Guid envioId, long version, CancellationToken ct)
    {
        var actor = await usuario.ObtenerAsync(ct);
        return await rutas.RecibirAsync(actor.OperadorId ?? throw new RepartoNoPermitidoException(), envioId, version, ct);
    }
    public async Task<bool> ConfirmarDestinoAsync(Guid envioId, long version, double latitud, double longitud, CancellationToken ct)
    {
        var actor = await usuario.ObtenerAsync(ct);
        return await rutas.ConfirmarDestinoAsync(actor.ComercioId ?? throw new RepartoNoPermitidoException(),
            actor.UsuarioId, envioId, version, latitud, longitud, ct);
    }
}
