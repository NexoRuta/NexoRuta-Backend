using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Administracion.Autenticacion;

public sealed class EfSesionesMovilesRepository(NexoRutaDbContext db) : ISesionesMovilesRepository
{
    public async Task CrearAsync(SesionMovil sesion, string tokenHash, CancellationToken ct)
    {
        db.SesionesMoviles.Add(sesion);
        db.TokensRenovacionMoviles.Add(new() { Hash = tokenHash, SesionId = sesion.Id });
        await db.SaveChangesAsync(ct);
    }

    public Task<SesionMovil?> ObtenerPorTokenAsync(string tokenHash, CancellationToken ct)
        => (from token in db.TokensRenovacionMoviles.AsNoTracking()
            join sesion in db.SesionesMoviles.AsNoTracking() on token.SesionId equals sesion.Id
            where token.Hash == tokenHash select sesion).SingleOrDefaultAsync(ct);

    public async Task<SesionMovil?> RotarAsync(string tokenHash, string nuevoHash, DateTimeOffset ahora, CancellationToken ct)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(ct);
        var id = await db.TokensRenovacionMoviles.Where(x => x.Hash == tokenHash)
            .Select(x => (Guid?)x.SesionId).SingleOrDefaultAsync(ct);
        if (id is null) return null;
        // El bloqueo de la familia serializa rotaciones y revocación, también desde otras instancias API.
        var sesion = await db.SesionesMoviles.FromSqlInterpolated(
            $"SELECT * FROM \"SesionesMoviles\" WHERE \"Id\" = {id.Value} FOR UPDATE").SingleAsync(ct);
        var token = await db.TokensRenovacionMoviles.SingleAsync(x => x.Hash == tokenHash, ct);
        if (!await db.Usuarios.AnyAsync(u => u.Id == sesion.UsuarioId && u.VersionCredencial == sesion.VersionCredencial, ct))
            sesion.Revocar(ahora);
        if (!sesion.AutorizarRenovacion(token.ConsumidoEnUtc.HasValue, ahora))
        {
            await db.SaveChangesAsync(ct);
            await transaccion.CommitAsync(ct);
            return null;
        }
        token.ConsumidoEnUtc = ahora;
        db.TokensRenovacionMoviles.Add(new() { Hash = nuevoHash, SesionId = sesion.Id });
        await db.SaveChangesAsync(ct);
        await transaccion.CommitAsync(ct);
        return sesion;
    }

    public async Task RevocarAsync(string tokenHash, DateTimeOffset ahora, CancellationToken ct)
    {
        // Un UPDATE toma el mismo bloqueo de fila que RotarAsync; no modifica otras sesiones del usuario.
        await db.SesionesMoviles.Where(s => s.RevocadaEnUtc == null &&
                db.TokensRenovacionMoviles.Any(t => t.Hash == tokenHash && t.SesionId == s.Id))
            .ExecuteUpdateAsync(x => x.SetProperty(s => s.RevocadaEnUtc, ahora), ct);
    }

    public Task<bool> EstaVigenteAsync(Guid sesionId, Guid usuarioId, Guid accesoId, long version,
        DateTimeOffset ahora, CancellationToken ct)
        => db.SesionesMoviles.AsNoTracking().AnyAsync(x => x.Id == sesionId && x.UsuarioId == usuarioId
            && x.AccesoId == accesoId && x.VersionSesion == version && x.RevocadaEnUtc == null && x.ExpiraEnUtc > ahora
            && db.Usuarios.Any(u => u.Id == x.UsuarioId && u.VersionCredencial == x.VersionCredencial), ct);
}
