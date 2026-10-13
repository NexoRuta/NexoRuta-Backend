using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Dtos;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Administracion.Repositories;

public sealed class EfRolesRepository(NexoRutaDbContext db) : IRolesRepository
{
    private IQueryable<Rol> Owned(ContextoUsuario actor)
        => db.Roles.Where(x => x.OperadorId == actor.OperadorId && x.ComercioId == actor.ComercioId);

    public async Task<IReadOnlyList<RolResumen>> ListarAsync(ContextoUsuario actor, CancellationToken ct)
        => await Owned(actor).AsNoTracking().OrderBy(x => x.Nombre).Select(x => new RolResumen(x.Id, x.Codigo, x.Nombre,
            (from grant in db.RolesPermisos join permission in db.Permisos on grant.PermisoId equals permission.Id
             where grant.RolId == x.Id orderby permission.Codigo select permission.Codigo).ToList())).ToListAsync(ct);

    public async Task<IReadOnlyList<PermisoResumen>> PermisosAsync(CancellationToken ct)
        => await db.Permisos.AsNoTracking().OrderBy(x => x.Codigo).Select(x => new PermisoResumen(x.Id, x.Codigo, x.Nombre)).ToListAsync(ct);

    public Task<Rol?> ObtenerAsync(ContextoUsuario actor, Guid id, CancellationToken ct)
        => Owned(actor).SingleOrDefaultAsync(x => x.Id == id, ct);

    public async Task CrearAsync(Rol role, CancellationToken ct) { db.Add(role); await db.SaveChangesAsync(ct); }
    public async Task GuardarAsync(CancellationToken ct) => await db.SaveChangesAsync(ct);

    public async Task<bool> EliminarAsync(ContextoUsuario actor, Guid id, CancellationToken ct)
    {
        var role = await ObtenerAsync(actor, id, ct);
        if (role is null) return false;
        if (await db.AccesosUsuario.AnyAsync(x => x.RolId == id, ct))
            throw new InvalidOperationException("An assigned role cannot be deleted.");
        db.RolesPermisos.RemoveRange(await db.RolesPermisos.Where(x => x.RolId == id).ToListAsync(ct));
        db.Remove(role);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> ActualizarPermisosAsync(ContextoUsuario actor, Guid id, IReadOnlyList<string> codigos, CancellationToken ct)
    {
        if (await ObtenerAsync(actor, id, ct) is null) return false;
        var codes = codigos.Distinct(StringComparer.Ordinal).ToArray();
        var permissions = await db.Permisos.Where(x => codes.Contains(x.Codigo)).ToListAsync(ct);
        if (permissions.Count != codes.Length) throw new ArgumentException("Unknown permission code.");
        var grants = await db.RolesPermisos.Where(x => x.RolId == id).ToListAsync(ct);
        db.RemoveRange(grants.Where(x => !permissions.Any(p => p.Id == x.PermisoId)));
        foreach (var permission in permissions.Where(p => !grants.Any(x => x.PermisoId == p.Id)))
            db.Add(new RolPermiso(id, permission.Id));
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> AsignarAsync(ContextoUsuario actor, Guid accesoId, Guid? rolId, CancellationToken ct)
    {
        var access = await db.AccesosUsuario.SingleOrDefaultAsync(x => x.Id == accesoId
            && x.OperadorId == actor.OperadorId && x.ComercioId == actor.ComercioId, ct);
        if (access is null) return false;
        if (rolId is null) access.RetirarRol();
        else
        {
            var role = await ObtenerAsync(actor, rolId.Value, ct);
            if (role is null) return false;
            access.AsignarRol(role);
        }
        await db.SaveChangesAsync(ct);
        return true;
    }
}
