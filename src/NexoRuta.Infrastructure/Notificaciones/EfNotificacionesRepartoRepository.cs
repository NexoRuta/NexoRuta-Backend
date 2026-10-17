using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Notificaciones.Dtos;
using NexoRuta.Application.Notificaciones.Repositories;
using NexoRuta.Application.Reparto.Exceptions;
using NexoRuta.Domain.Reparto;
using NexoRuta.Infrastructure.Persistence;
namespace NexoRuta.Infrastructure.Notificaciones;

public sealed class EfNotificacionesRepartoRepository(NexoRutaDbContext db, CifradorRegistroPush cifrador, TimeProvider reloj) : INotificacionesRepartoRepository
{
    public async Task RegistrarInstalacionAsync(Guid operadorId, Guid accesoId, Guid sesionId, Guid instalacionId,
        string plataforma, string fid, bool permitida, CancellationToken ct)
    {
        if (instalacionId == Guid.Empty || plataforma is not ("Android" or "iOS") || fid.Length is < 20 or > 128 || fid.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("El registro push requiere instalación, plataforma y FID válidos.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Contexto(operadorId, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Operadores\" WHERE \"Id\"={operadorId} FOR UPDATE", ct);
        var ahora = reloj.GetUtcNow();
        if (!await db.SesionesMoviles.AnyAsync(s => s.Id == sesionId && s.AccesoId == accesoId && s.RevocadaEnUtc == null && s.ExpiraEnUtc > ahora
            && db.AccesosUsuario.Any(a => a.Id == accesoId && a.Activo && a.OperadorId == operadorId && a.VersionSesion == s.VersionSesion), ct))
            throw new RepartoNoPermitidoException();
        var instalacion = await db.InstalacionesMoviles.SingleOrDefaultAsync(x => x.Id == instalacionId, ct);
        if (instalacion is not null && (instalacion.AccesoId != accesoId || instalacion.OperadorId != operadorId))
            throw new ConflictoRepartoException("La instalación ya tiene otro contexto de cuenta.");
        if (instalacion is null)
        {
            instalacion = new() { Id = instalacionId, OperadorId = operadorId, AccesoId = accesoId };
            db.InstalacionesMoviles.Add(instalacion);
        }
        instalacion.SesionId = sesionId; instalacion.Plataforma = plataforma; instalacion.Permitida = permitida;
        instalacion.Activa = true; instalacion.ActualizadaEnUtc = ahora; cifrador.Cifrar(instalacion, fid);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    public async Task<IReadOnlyList<AvisoRepartoDatos>> ListarAsync(Guid operadorId, Guid accesoId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); await Contexto(operadorId, ct);
        var avisos = await (from a in db.AvisosReparto.AsNoTracking() join h in db.HojasRuta on a.RutaId equals h.Id
            where a.OperadorId == operadorId && a.AccesoId == accesoId && a.ConfirmadoEnUtc == null && h.Estado != EstadoHojaRuta.Finalizada
            orderby a.CreadoEnUtc descending
            select new AvisoRepartoDatos(a.Id, a.RutaId, a.EnvioId, a.VersionEnvio, a.Tipo, a.Motivo, a.CreadoEnUtc, true, a.ConfirmadoEnUtc))
            .Take(100).ToArrayAsync(ct);
        await tx.CommitAsync(ct); return avisos;
    }
    public async Task<AvisoRepartoDatos?> ObtenerAsync(Guid operadorId, Guid accesoId, Guid id, string? registro, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); await Contexto(operadorId, ct);
        var aviso = await db.AvisosReparto.FromSqlInterpolated($"SELECT * FROM \"AvisosReparto\" WHERE \"Id\"={id} AND \"OperadorId\"={operadorId} AND \"AccesoId\"={accesoId} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (aviso is null) return null;
        if (registro is not null) { aviso.Registrar(registro, reloj.GetUtcNow()); await db.SaveChangesAsync(ct); }
        var vigente = aviso.ConfirmadoEnUtc is null && await db.HojasRuta.AnyAsync(h => h.Id == aviso.RutaId && h.OperadorId == operadorId && h.Estado != EstadoHojaRuta.Finalizada, ct);
        var resultado = new AvisoRepartoDatos(aviso.Id, aviso.RutaId, aviso.EnvioId, aviso.VersionEnvio, aviso.Tipo, aviso.Motivo, aviso.CreadoEnUtc, vigente, aviso.ConfirmadoEnUtc);
        await tx.CommitAsync(ct); return resultado;
    }
    private Task Contexto(Guid operadorId, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('nexoruta.operador_id', {operadorId.ToString()}, true)", ct);
}
