using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Notificaciones.Dtos;
using NexoRuta.Application.Notificaciones.Repositories;
using NexoRuta.Application.Reparto.Exceptions;
using NexoRuta.Application.Reparto.Repositories;
namespace NexoRuta.Application.Notificaciones.UseCases;
public sealed class NotificacionesRepartoUseCase(IUsuarioActual usuario, IRepartidoresRepository repartidores, INotificacionesRepartoRepository repositorio)
{
    private async Task<(Guid Op, Guid Acceso)> Actor(CancellationToken ct)
    {
        var actor = await usuario.ObtenerAsync(ct); var op = actor.OperadorId ?? throw new RepartoNoPermitidoException();
        if (await repartidores.ObtenerActivoAsync(op, actor.AccesoId, ct) is null) throw new RepartoNoPermitidoException();
        return (op, actor.AccesoId);
    }
    public async Task RegistrarAsync(Guid sesion, Guid instalacion, string plataforma, string fid, bool permitida, CancellationToken ct)
    { var actor = await Actor(ct); await repositorio.RegistrarInstalacionAsync(actor.Op, actor.Acceso, sesion, instalacion, plataforma, fid, permitida, ct); }
    public async Task<bool> DesactivarAsync(Guid instalacion, CancellationToken ct)
    {
        var actor = await usuario.ObtenerAsync(ct);
        var op = actor.OperadorId ?? throw new RepartoNoPermitidoException();
        return await repositorio.DesactivarInstalacionAsync(op, actor.AccesoId, instalacion, ct);
    }
    public async Task<IReadOnlyList<AvisoRepartoDatos>> ListarAsync(CancellationToken ct)
    { var actor = await Actor(ct); return await repositorio.ListarAsync(actor.Op, actor.Acceso, ct); }
    public async Task<AvisoRepartoDatos?> ObtenerAsync(Guid id, string? registro, CancellationToken ct)
    { var actor = await Actor(ct); return await repositorio.ObtenerAsync(actor.Op, actor.Acceso, id, registro, ct); }
}
