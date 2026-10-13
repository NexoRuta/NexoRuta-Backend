
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Administracion.Repositories;

public sealed class EfPermisosUsuarioRepository(
    NexoRutaDbContext db) : IPermisosUsuarioRepository
{
    public Task<bool> TienePermisoAsync(
        Guid usuarioId,
        Guid accesoId,
        string codigoPermiso,
        CancellationToken cancellationToken = default)
    {
        return (
            from acceso in db.AccesosUsuario.AsNoTracking()
            join usuario in db.Usuarios.AsNoTracking() on acceso.UsuarioId equals usuario.Id
            join rol in db.Roles.AsNoTracking() on acceso.RolId equals rol.Id
            join rolPermiso in db.RolesPermisos.AsNoTracking()
                on acceso.RolId equals rolPermiso.RolId
            join permiso in db.Permisos.AsNoTracking()
                on rolPermiso.PermisoId equals permiso.Id
            where acceso.Id == accesoId
                && acceso.UsuarioId == usuarioId
                && permiso.Codigo == codigoPermiso
                && usuario.PasswordHash != null && usuario.PasswordHash != ""
                && rol.OrganizacionId == acceso.OrganizacionId
            select permiso.Id
        ).AnyAsync(cancellationToken);
    }
}
