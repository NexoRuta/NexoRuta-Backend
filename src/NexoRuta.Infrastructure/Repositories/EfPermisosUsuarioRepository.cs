
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.Domain.Administracion;

namespace NexoRuta.Infrastructure.Administracion.Repositories;

public sealed class EfPermisosUsuarioRepository(
    NexoRutaDbContext db) : IPermisosUsuarioRepository
{
    public async Task<IReadOnlyList<string>> ListarCodigosAsync(
        Guid usuarioId,
        Guid accesoId,
        CancellationToken cancellationToken = default)
    {
        return await (
            from acceso in db.AccesosUsuario.AsNoTracking()
            join usuario in db.Usuarios.AsNoTracking() on acceso.UsuarioId equals usuario.Id
            join rol in db.Roles.AsNoTracking() on acceso.RolId equals rol.Id
            join rolPermiso in db.RolesPermisos.AsNoTracking()
                on acceso.RolId equals rolPermiso.RolId
            join permiso in db.Permisos.AsNoTracking()
                on rolPermiso.PermisoId equals permiso.Id
            where acceso.Id == accesoId
                && acceso.UsuarioId == usuarioId
                && acceso.Activo
                && usuario.PasswordHash != null && usuario.PasswordHash != ""
                && rol.OrganizacionId == acceso.OrganizacionId
            select permiso.Codigo
        ).Distinct().OrderBy(code => code).ToListAsync(cancellationToken);
    }

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
                && acceso.Activo
                && permiso.Codigo == codigoPermiso
                && usuario.PasswordHash != null && usuario.PasswordHash != ""
                && rol.OrganizacionId == acceso.OrganizacionId
            select permiso.Id
        ).AnyAsync(cancellationToken);
    }

    public Task<bool> PuedeGestionarDecisionesOperativasEnvioAsync(
        Guid usuarioId, Guid accesoId, Guid rolId, Guid operadorId, CancellationToken cancellationToken = default)
        => (from acceso in db.AccesosUsuario.AsNoTracking()
            join usuario in db.Usuarios.AsNoTracking() on acceso.UsuarioId equals usuario.Id
            join rol in db.Roles.AsNoTracking() on acceso.RolId equals rol.Id
            join grant in db.RolesPermisos.AsNoTracking() on rol.Id equals grant.RolId
            join permiso in db.Permisos.AsNoTracking() on grant.PermisoId equals permiso.Id
            join operador in db.Operadores.AsNoTracking() on acceso.OperadorId equals (Guid?)operador.Id
            where acceso.Id == accesoId && acceso.UsuarioId == usuarioId && acceso.RolId == rolId
                && acceso.OperadorId == operadorId && acceso.ComercioId == null && acceso.Activo
                && operador.Estado == EstadoOrganizacion.Activa
                && usuario.PasswordHash != null && usuario.PasswordHash != ""
                && rol.Id == rolId && rol.OperadorId == operadorId && rol.ComercioId == null
                && rol.OrganizacionId == operadorId && rol.EsProtegidoSistema
                && (rol.Codigo == CodigosRolesSistema.AdministradorOperador
                    || rol.Codigo == CodigosRolesSistema.Administrador
                    || rol.Codigo == CodigosRolesSistema.Dueno
                    || rol.Codigo == CodigosRolesSistema.DespachadorOperador)
                && permiso.Codigo == "envios.gestionar"
            select acceso.Id).AnyAsync(cancellationToken);
}
