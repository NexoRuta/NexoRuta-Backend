using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Administracion;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Persistence;
using Npgsql;

namespace NexoRuta.Infrastructure.Administracion.Repositories;

public sealed class EfRegistroRepository(NexoRutaDbContext db, IGeneradorPasswordHash hasher) : IRegistroRepository
{
    public async Task<IdentidadSinOrganizacion> RegistrarAsync(string email, string password, CancellationToken ct)
    {
        var normalized = ValidacionCredenciales.Validar(email, password);
        if (await db.Usuarios.AnyAsync(x => x.Email.Trim().ToLower() == normalized, ct))
            throw new CorreoYaRegistradoException();
        var user = new Usuario(normalized, hasher.Generar(normalized, password));
        db.Usuarios.Add(user);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException error) when (error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        { throw new CorreoYaRegistradoException(); }
        return new(user.Id, user.Email);
    }

    public Task<IdentidadSinOrganizacion?> ObtenerSinOrganizacionAsync(Guid usuarioId, CancellationToken ct)
        => db.Usuarios.AsNoTracking()
            .Where(x => x.Id == usuarioId && x.PasswordHash != null && x.PasswordHash != "" &&
                !db.AccesosUsuario.Any(a => a.UsuarioId == x.Id) && !db.AccesosPlataforma.Any(a => a.UsuarioId == x.Id))
            .Select(x => new IdentidadSinOrganizacion(x.Id, x.Email)).SingleOrDefaultAsync(ct);
}
