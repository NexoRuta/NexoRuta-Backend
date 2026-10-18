using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Dtos;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Administracion.Repositories;

public sealed class EfAccesosUsuarioRepository(NexoRutaDbContext db) : IAccesosUsuarioRepository
{

    private IQueryable<ContextoUsuario> Consulta(
        IQueryable<AccesoUsuario> accesos) =>
        from acceso in accesos
        join usuario in db.Usuarios
            on acceso.UsuarioId equals usuario.Id
        join rol in db.Roles
            on acceso.RolId equals rol.Id
        join operador in db.Operadores
            on acceso.OperadorId equals (Guid?)operador.Id into operadores
        from operador in operadores.DefaultIfEmpty()
        join comercio in db.Comercios
            on acceso.ComercioId equals (Guid?)comercio.Id into comercios
        from comercio in comercios.DefaultIfEmpty()
        where usuario.PasswordHash != null && usuario.PasswordHash != ""
            && acceso.OrganizacionId == rol.OrganizacionId
        orderby usuario.Email, operador.Nombre, comercio.Nombre
        select new ContextoUsuario(
            acceso.Id,
            usuario.Id,
            acceso.OperadorId,
            acceso.ComercioId,
            usuario.Email!,
            operador == null ? null : operador.Nombre,
            comercio == null ? null : comercio.Nombre,
            rol.Id,
            rol.Codigo,
            acceso.VersionSesion,
            operador != null ? operador.Estado : comercio!.Estado);


    public async Task<IReadOnlyList<ContextoUsuario>> ListarAsync(
        TipoAccesoUsuario tipo, CancellationToken cancellationToken = default)
        => await Consulta(db.AccesosUsuario.Where(x => tipo == TipoAccesoUsuario.Operador
                ? x.OperadorId != null
                : x.ComercioId != null))
            .ToListAsync(cancellationToken);

    public async Task<ContextoUsuario?> ObtenerAsync(Guid accesoId, CancellationToken cancellationToken = default)
    {
        var row = await db.Database.SqlQuery<AccesoLookupRow>($"""
            SELECT * FROM nexoruta_app.lookup_access({accesoId})
            """).SingleOrDefaultAsync(cancellationToken);
        if (row is null || !Enum.TryParse<EstadoOrganizacion>(row.Estado, out var estado)) return null;
        return new(row.AccesoId, row.UsuarioId, row.OperadorId, row.ComercioId, row.UsuarioEmail,
            row.OperadorNombre, row.ComercioNombre, row.RolId, row.RolCodigo, row.VersionSesion, estado);
    }

    public Task<bool> EsPropietarioAsync(Guid usuarioId, Guid accesoId, CancellationToken cancellationToken = default)
        => db.AccesosUsuario.AsNoTracking().AnyAsync(
            x => x.UsuarioId == usuarioId && x.Id == accesoId && x.EsPropietario,
            cancellationToken);

    private sealed record AccesoLookupRow(Guid AccesoId, Guid UsuarioId, Guid? OperadorId, Guid? ComercioId,
        string UsuarioEmail, string? OperadorNombre, string? ComercioNombre, Guid RolId, string RolCodigo,
        long VersionSesion, string Estado);
}
