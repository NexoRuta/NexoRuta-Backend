using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Dtos;
using NexoRuta.Application.Administracion.Exceptions;
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
             where grant.RolId == x.Id orderby permission.Codigo select permission.Codigo).ToList())
        {
            EsProtegidoSistema = x.EsProtegidoSistema,
            EsPropio = x.Id == actor.RolId || db.AccesosUsuario.Any(a => a.UsuarioId == actor.UsuarioId && a.RolId == x.Id),
            EsRolPropietario = db.AccesosUsuario.Any(a => a.EsPropietario && a.RolId == x.Id)
        }).ToListAsync(ct);

    public async Task<IReadOnlyList<PermisoResumen>> PermisosAsync(ContextoUsuario actor, CancellationToken ct)
    {
        var applicableCodes = CatalogoPermisos.Para(actor.TipoAcceso).Select(x => x.Codigo).ToArray();
        return await db.Permisos.AsNoTracking().Where(x => applicableCodes.Contains(x.Codigo))
            .OrderBy(x => x.Codigo).Select(x => new PermisoResumen(x.Id, x.Codigo, x.Nombre)).ToListAsync(ct);
    }

    public Task<Rol?> ObtenerAsync(ContextoUsuario actor, Guid id, CancellationToken ct)
        => Owned(actor).SingleOrDefaultAsync(x => x.Id == id, ct);

    public async Task CrearAsync(Rol role, CancellationToken ct) { db.Add(role); await db.SaveChangesAsync(ct); }

    public async Task<bool> RenombrarAsync(ContextoUsuario actor, Guid id, string nombre, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var actorAccesses = await LockActorAccessesAsync(actor.UsuarioId, ct);
        var role = await LockOwnedRoleAsync(actor, id, ct);
        if (role is null) return false;
        await EnsureEditableAsync(actor, actorAccesses, role, ct);
        role.Renombrar(nombre);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<bool> EliminarAsync(ContextoUsuario actor, Guid id, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var actorAccesses = await LockActorAccessesAsync(actor.UsuarioId, ct);
        var role = await LockOwnedRoleAsync(actor, id, ct);
        if (role is null) return false;
        await EnsureEditableAsync(actor, actorAccesses, role, ct);
        if (await db.AccesosUsuario.AnyAsync(x => x.RolId == id, ct))
            throw new InvalidOperationException("An assigned role cannot be deleted.");
        db.RolesPermisos.RemoveRange(await db.RolesPermisos.Where(x => x.RolId == id).ToListAsync(ct));
        db.Remove(role);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<bool> ActualizarPermisosAsync(ContextoUsuario actor, Guid id, IReadOnlyList<string> codigos, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var actorAccesses = await LockActorAccessesAsync(actor.UsuarioId, ct);
        var role = await LockOwnedRoleAsync(actor, id, ct);
        if (role is null) return false;
        await EnsureEditableAsync(actor, actorAccesses, role, ct);
        var codes = codigos.Distinct(StringComparer.Ordinal).ToArray();
        var applicableCodes = CatalogoPermisos.Para(actor.TipoAcceso)
            .Select(x => x.Codigo).ToHashSet(StringComparer.Ordinal);
        if (codes.Any(code => !applicableCodes.Contains(code)))
            throw new ArgumentException("A permission is not applicable to this organization.");
        var permissions = await db.Permisos.Where(x => codes.Contains(x.Codigo)).ToListAsync(ct);
        if (permissions.Count != codes.Length) throw new ArgumentException("Unknown permission code.");
        var grants = await db.RolesPermisos.Where(x => x.RolId == id).ToListAsync(ct);
        db.RemoveRange(grants.Where(x => !permissions.Any(p => p.Id == x.PermisoId)));
        foreach (var permission in permissions.Where(p => !grants.Any(x => x.PermisoId == p.Id)))
            db.Add(new RolPermiso(id, permission.Id));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<bool> AsignarAsync(ContextoUsuario actor, Guid accesoId, Guid? rolId, bool puedeAsignarRolProtegido, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var access = await LockOwnedAccessAsync(actor, accesoId, ct);
        if (access is null) return false;
        if (await db.AccesosUsuario.AsNoTracking().AnyAsync(x => x.Id == access.Id && x.EsPropietario, ct) ||
            access.UsuarioId == actor.UsuarioId)
            throw new AccesoNoPermitidoException();

        if (rolId is null)
        {
            access.RetirarRol();
        }
        else
        {
            var role = await LockOwnedRoleAsync(actor, rolId.Value, ct);
            if (role is null) return false;
            if (role.EsProtegidoSistema && !puedeAsignarRolProtegido)
                throw new AccesoNoPermitidoException();
            access.AsignarRol(role);
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private async Task<List<AccesoUsuario>> LockActorAccessesAsync(Guid usuarioId, CancellationToken ct)
        => await db.AccesosUsuario.FromSqlInterpolated($"""
            SELECT * FROM "AccesosUsuario"
            WHERE "UsuarioId" = {usuarioId}
            ORDER BY "Id"
            FOR UPDATE
            """).AsNoTracking().ToListAsync(ct);

    private async Task<Rol?> LockOwnedRoleAsync(ContextoUsuario actor, Guid id, CancellationToken ct)
    {
        var role = await db.Roles.FromSqlInterpolated($"""
            SELECT * FROM "Roles"
            WHERE "Id" = {id}
              AND "OperadorId" IS NOT DISTINCT FROM {actor.OperadorId}
              AND "ComercioId" IS NOT DISTINCT FROM {actor.ComercioId}
            FOR UPDATE
            """).SingleOrDefaultAsync(ct);
        if (role is not null)
            await db.Entry(role).ReloadAsync(ct);
        return role;
    }

    private Task<AccesoUsuario?> LockOwnedAccessAsync(ContextoUsuario actor, Guid id, CancellationToken ct)
        => db.AccesosUsuario.FromSqlInterpolated($"""
            SELECT * FROM "AccesosUsuario"
            WHERE "Id" = {id}
              AND "OperadorId" IS NOT DISTINCT FROM {actor.OperadorId}
              AND "ComercioId" IS NOT DISTINCT FROM {actor.ComercioId}
            FOR UPDATE
            """).SingleOrDefaultAsync(ct);

    private async Task EnsureEditableAsync(ContextoUsuario actor, IEnumerable<AccesoUsuario> actorAccesses, Rol role, CancellationToken ct)
    {
        if (role.EsProtegidoSistema || role.Id == actor.RolId || actorAccesses.Any(x => x.RolId == role.Id))
            throw new AccesoNoPermitidoException();
        if (await db.AccesosUsuario.AnyAsync(x => x.RolId == role.Id && x.EsPropietario, ct))
            throw new AccesoNoPermitidoException();
    }
}
