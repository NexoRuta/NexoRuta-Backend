using Microsoft.EntityFrameworkCore;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Reparto;
using NexoRuta.Infrastructure.Persistence;
namespace NexoRuta.Infrastructure.Notificaciones;
public sealed class EfDespachoPushRepository(NexoRutaDbContext db)
{
    public async Task<LotePush?> PendientesAsync(Guid operadorId, Guid avisoId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); await Contexto(operadorId, ct);
        var aviso = await db.AvisosReparto.AsNoTracking().SingleOrDefaultAsync(a => a.OperadorId == operadorId && a.Id == avisoId && a.ConfirmadoEnUtc == null, ct);
        if (aviso is null || !await db.HojasRuta.AnyAsync(h => h.OperadorId == operadorId && h.Id == aviso.RutaId
                && h.Estado != EstadoHojaRuta.Finalizada, ct)
            || !await db.Envios.AnyAsync(e => e.OperadorId == operadorId && e.Id == aviso.EnvioId && e.EntregaFisicaEnUtc == null, ct)) return null;
        var ahora = DateTimeOffset.UtcNow;
        var filas = await (from i in db.InstalacionesMoviles.AsNoTracking()
            join s in db.SesionesMoviles on i.SesionId equals s.Id
            join a in db.AccesosUsuario on i.AccesoId equals a.Id
            join r in db.Repartidores on i.AccesoId equals r.AccesoId
            join o in db.Operadores on i.OperadorId equals o.Id
            where i.OperadorId == operadorId && i.AccesoId == aviso.AccesoId && i.Activa && i.Permitida
                && s.AccesoId == i.AccesoId && s.RevocadaEnUtc == null && s.ExpiraEnUtc > ahora && s.VersionSesion == a.VersionSesion
                && a.Activo && a.OperadorId == operadorId && r.Activo && o.Estado == EstadoOrganizacion.Activa
                && db.RolesPermisos.Any(rp => rp.RolId == a.RolId && db.Permisos.Any(p => p.Id == rp.PermisoId && p.Codigo == "reparto.ejecutar"))
                && !db.EntregasPush.Any(e => e.OperadorId == operadorId && e.AvisoId == avisoId && e.FidHash == i.FidHash)
            orderby i.ActualizadaEnUtc descending
            select i).Take(100).ToArrayAsync(ct);
        await tx.CommitAsync(ct);
        return new() { Aviso = aviso, Instalaciones = filas.DistinctBy(x => x.FidHash).ToArray() };
    }
    public async Task RegistrarAsync(Guid operadorId, Guid avisoId, Guid instalacionId, string fidHash, string resultado, string? idFcm, CancellationToken ct)
    {
        if (resultado is not ("FcmAceptada" or "FidInvalido")) throw new ArgumentException("Resultado push no válido.");
        await using var tx = await db.Database.BeginTransactionAsync(ct); await Contexto(operadorId, ct);
        if (!await db.EntregasPush.AnyAsync(e => e.AvisoId == avisoId && e.InstalacionId == instalacionId && e.FidHash == fidHash, ct))
        {
            db.EntregasPush.Add(new() { OperadorId = operadorId, AvisoId = avisoId, InstalacionId = instalacionId,
                FidHash = fidHash, Resultado = resultado, IdFcm = idFcm, RegistradaEnUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(ct);
        }
        if (resultado == "FidInvalido")
            await db.InstalacionesMoviles.Where(i => i.OperadorId == operadorId && i.Id == instalacionId && i.FidHash == fidHash)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.Activa, false), ct);
        await tx.CommitAsync(ct);
    }
    private Task Contexto(Guid op, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('nexoruta.operador_id', {op.ToString()}, true)", ct);
}
