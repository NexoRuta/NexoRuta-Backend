using Microsoft.EntityFrameworkCore;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Reparto.Dtos;
using NexoRuta.Application.Reparto.Exceptions;
using NexoRuta.Domain.Reparto;
namespace NexoRuta.Infrastructure.Reparto;
public sealed partial class EfConfiguracionRepartoRepository
{
    public async Task<BaseOperativaDatos?> BaseAsync(Guid op, CancellationToken ct)
    {
        await using var tx = await db.BeginOrJoinAsync(ct); await Contexto(op, ct);
        var b = await db.BasesOperativas.AsNoTracking().SingleOrDefaultAsync(x => x.OperadorId == op, ct);
        await tx.CommitAsync(ct); return b is null ? null : Datos(b);
    }
    public async Task<BaseOperativaDatos> GuardarBaseAsync(Guid op, GuardarBaseOperativaDatos datos, CancellationToken ct)
    {
        await using var tx = await db.BeginOrJoinAsync(ct); await Contexto(op, ct); await BloquearOperador(op, ct);
        var b = await db.BasesOperativas.SingleOrDefaultAsync(x => x.OperadorId == op, ct);
        if (datos.Version < 0 || datos.Version != (b?.Version ?? 0)) throw new ConflictoRepartoException("La base cambió; conservá tus datos y recargá antes de confirmar otra versión.");
        if (b is null) { b = new(op, datos.Direccion, datos.ServicioPredeterminadoMinutos, datos.MargenRetornoMinutos); db.BasesOperativas.Add(b); }
        else b.Actualizar(datos.Direccion, datos.ServicioPredeterminadoMinutos, datos.MargenRetornoMinutos);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Datos(b);
    }
    public async Task<BaseOperativaDatos?> ConfirmarBaseAsync(ContextoUsuario actor, long version, double latitud, double longitud, DateTimeOffset ahora, CancellationToken ct)
    {
        var op = actor.OperadorId ?? throw new RepartoNoPermitidoException();
        await using var tx = await db.BeginOrJoinAsync(ct); await Contexto(op, ct); await BloquearOperador(op, ct);
        var b = await db.BasesOperativas.SingleOrDefaultAsync(x => x.OperadorId == op, ct); if (b is null) return null;
        if (b.Version != version) throw new ConflictoRepartoException("La base cambió; revisá la dirección y su versión antes de confirmar el punto.");
        b.Confirmar(latitud, longitud, actor.AccesoId, ahora);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Datos(b);
    }
    private static BaseOperativaDatos Datos(BaseOperativa b) => new(b.Version, b.Direccion, b.Latitud, b.Longitud, b.ConfirmadaEnUtc, b.ConfirmadaPorAccesoId, b.ServicioPredeterminadoMinutos, b.MargenRetornoMinutos);
}
