using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using NexoRuta.Application.Administracion;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Persistence;
using Npgsql;

namespace NexoRuta.Infrastructure.Administracion.Repositories;

public sealed class EfRegistroRepository(NexoRutaDbContext db, UserManager<Usuario> identityUsers) : IRegistroRepository
{
    public async Task<IdentidadSinOrganizacion> RegistrarAsync(string email, string password, CancellationToken ct)
    {
        var normalized = ValidacionCredenciales.Validar(email, password);
        if (await db.Usuarios.AnyAsync(x => x.Email!.Trim().ToLower() == normalized, ct))
            throw new CorreoYaRegistradoException();
        var user = new Usuario(normalized);
        try
        {
            var result = await identityUsers.CreateAsync(user, password);
            if (!result.Succeeded)
            {
                if (result.Errors.Any(x => x.Code is "DuplicateEmail" or "DuplicateUserName"))
                    throw new CorreoYaRegistradoException();
                throw new ArgumentException("La cuenta no cumple las reglas de Identity.", nameof(password));
            }
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        { throw new CorreoYaRegistradoException(); }
        return new(user.Id, user.Email!);
    }

    public async Task<IdentidadSinOrganizacion?> ObtenerSinOrganizacionAsync(Guid usuarioId, CancellationToken ct)
    {
        var row = await db.Database.SqlQuery<UnassignedIdentityRow>($"""
            SELECT * FROM nexoruta_app.lookup_unassigned_identity({usuarioId})
            """).SingleOrDefaultAsync(ct);
        return row is null ? null : new(row.UsuarioId, row.Email);
    }

    private sealed record UnassignedIdentityRow(Guid UsuarioId, string Email);
}
