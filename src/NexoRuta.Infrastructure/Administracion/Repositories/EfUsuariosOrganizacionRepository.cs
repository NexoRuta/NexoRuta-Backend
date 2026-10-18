
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Administracion.Dtos;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.Domain.Administracion;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Context;
using Microsoft.AspNetCore.Identity;
namespace NexoRuta.Infrastructure.Administracion.Repositories;

public sealed class EfUsuariosOrganizacionRepository(
    NexoRutaDbContext db,
    UserManager<Usuario> identityUsers)
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
                usuario.Email!,
                acceso.RolId,
                rol == null ? null : rol.Codigo,
                rol == null ? null : rol.Nombre,
                acceso.Activo,
                usuario.Version,
                rol != null && rol.EsProtegidoSistema,
                acceso.EsPropietario)
        ).ToListAsync(cancellationToken);
    }

    public Task<bool> ExisteEmailAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        var normalizado = email.Trim().ToLowerInvariant();

        return db.Usuarios.AsNoTracking().AnyAsync(
            x => x.Email!.ToLower() == normalizado,
            cancellationToken);
    }

    public async Task GuardarNuevoAsync(
        Usuario usuario,
        string password,
        AccesoUsuario acceso,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.BeginOrJoinAsync(cancellationToken);
        try
        {
            var result = await identityUsers.CreateAsync(usuario, password);
            if (!result.Succeeded)
            {
                if (result.Errors.Any(x => x.Code is "DuplicateEmail" or "DuplicateUserName"))
                    throw new CorreoYaRegistradoException();
                throw new ArgumentException("La cuenta no cumple las reglas de Identity.", nameof(password));
            }
            db.AccesosUsuario.Add(acceso);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
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

        var esPropietario = await db.AccesosUsuario.AsNoTracking().AnyAsync(
            x => x.UsuarioId == usuarioId
                && x.OperadorId == actor.OperadorId
                && x.ComercioId == actor.ComercioId
                && x.EsPropietario,
            cancellationToken);
        if (esPropietario)
            throw new AccesoNoPermitidoException();

        var acceso = await db.AccesosUsuario.SingleOrDefaultAsync(
            x => x.UsuarioId == usuarioId
                && x.OperadorId == actor.OperadorId
                && x.ComercioId == actor.ComercioId,
            cancellationToken);

        if (acceso is null)
            return false;

        if (acceso.EsPropietario)
            throw new AccesoNoPermitidoException();

        acceso.CambiarEstado(activo);

        await db.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<bool> CambiarEmailAsync(
        ContextoUsuario actor, Guid usuarioId, string emailNormalizado,
        long version, CancellationToken cancellationToken = default)
    {
        if (actor.OperadorId.HasValue == actor.ComercioId.HasValue)
            throw new AccesoNoPermitidoException();

        await using var transaction = await db.BeginOrJoinAsync(cancellationToken);
        var acceso = await db.AccesosUsuario.FromSqlInterpolated($"""
            SELECT * FROM "AccesosUsuario"
            WHERE "UsuarioId" = {usuarioId}
              AND "OperadorId" IS NOT DISTINCT FROM {actor.OperadorId}
              AND "ComercioId" IS NOT DISTINCT FROM {actor.ComercioId}
            FOR UPDATE
            """).SingleOrDefaultAsync(cancellationToken);
        if (acceso is null) return false;
        await db.Entry(acceso).ReloadAsync(cancellationToken);
        if (acceso.EsPropietario || acceso.UsuarioId == actor.UsuarioId ||
            await db.Roles.AnyAsync(x => x.Id == acceso.RolId && x.EsProtegidoSistema, cancellationToken))
            throw new AccesoNoPermitidoException();

        var usuario = await db.Usuarios.SingleAsync(x => x.Id == usuarioId, cancellationToken);
        if (usuario.Version != version)
            throw new DbUpdateConcurrencyException("El correo cambió durante la operación. Recargá e intentá nuevamente.");
        if (usuario.Email!.Trim().ToLowerInvariant() == emailNormalizado) return true;
        usuario.CambiarEmail(emailNormalizado);

        acceso.RevocarSesiones();
        try
        {
            var emailResult = await identityUsers.SetEmailAsync(usuario, emailNormalizado);
            EnsureIdentityEmailUpdate(emailResult);
            var userNameResult = await identityUsers.SetUserNameAsync(usuario, emailNormalizado);
            EnsureIdentityEmailUpdate(userNameResult);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException
            { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            throw new CorreoYaRegistradoException();
        }
        return true;
    }

    private static void EnsureIdentityEmailUpdate(IdentityResult result)
    {
        if (result.Succeeded) return;
        if (result.Errors.Any(x => x.Code is "DuplicateEmail" or "DuplicateUserName"))
            throw new CorreoYaRegistradoException();
        if (result.Errors.Any(x => x.Code == "ConcurrencyFailure"))
            throw new DbUpdateConcurrencyException("The account changed during the email update; reload and retry.");
        throw new InvalidOperationException("Identity rejected the account email update.");
    }

}
