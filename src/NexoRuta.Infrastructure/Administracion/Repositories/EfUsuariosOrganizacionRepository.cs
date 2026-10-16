
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Administracion.Dtos;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.Domain.Administracion;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Context;
namespace NexoRuta.Infrastructure.Administracion.Repositories;

public sealed class EfUsuariosOrganizacionRepository(
    NexoRutaDbContext db)
    : IUsuariosOrganizacionRepository
{
    public async Task<IReadOnlyList<UsuarioOrganizacionResumen>>
        ListarAsync(
            Guid? operadorId,
            Guid? comercioId,
            CancellationToken cancellationToken = default)
    {
        if (operadorId.HasValue == comercioId.HasValue ||
            operadorId == Guid.Empty ||
            comercioId == Guid.Empty)
        {
            throw new ArgumentException(
                "Se requiere una organización válida.");
        }

        return await (
            from acceso in db.AccesosUsuario.AsNoTracking()
            join usuario in db.Usuarios.AsNoTracking()
                on acceso.UsuarioId equals usuario.Id
            join rol in db.Roles.AsNoTracking()
                on acceso.RolId equals (Guid?)rol.Id into roles
            from rol in roles.DefaultIfEmpty()
            where acceso.OperadorId == operadorId
                && acceso.ComercioId == comercioId
            orderby usuario.Email, usuario.Id
            select new UsuarioOrganizacionResumen(
                usuario.Id,
                acceso.Id,
                usuario.Email,
                acceso.RolId,
                rol == null ? null : rol.Codigo,
                rol == null ? null : rol.Nombre,
                acceso.Activo)
        ).ToListAsync(cancellationToken);
    }

    public Task<bool> ExisteEmailAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        var normalizado = email.Trim().ToLowerInvariant();

        return db.Usuarios.AsNoTracking().AnyAsync(
            x => x.Email.ToLower() == normalizado,
            cancellationToken);
    }

    public async Task GuardarNuevoAsync(
        Usuario usuario,
        AccesoUsuario acceso,
        CancellationToken cancellationToken = default)
    {
        db.Usuarios.Add(usuario);
        db.AccesosUsuario.Add(acceso);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is Npgsql.PostgresException
            {
                SqlState: Npgsql.PostgresErrorCodes.UniqueViolation
            })
        {
            throw new CorreoYaRegistradoException();
        }
    }

    public async Task<bool> CambiarEstadoAsync(
        ContextoUsuario actor,
        Guid usuarioId,
        bool activo,
        CancellationToken cancellationToken = default)
    {
        if (actor.OperadorId.HasValue == actor.ComercioId.HasValue)
        {
            throw new InvalidOperationException(
                "La organización del usuario no es válida.");
        }

        var acceso = await db.AccesosUsuario.SingleOrDefaultAsync(
            x => x.UsuarioId == usuarioId
                && x.OperadorId == actor.OperadorId
                && x.ComercioId == actor.ComercioId,
            cancellationToken);

        if (acceso is null)
            return false;

        acceso.CambiarEstado(activo);

        await db.SaveChangesAsync(cancellationToken);

        return true;
    }

}
