
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Administracion.Dtos;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Administracion.Autenticacion;

public sealed class EfUsuariosLoginRepository(
    NexoRutaDbContext db) : IUsuariosLoginRepository
{
    public Task<UsuarioLogin?> ObtenerPorEmailAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        var emailNormalizado = email.Trim().ToLowerInvariant();

        return (
            from usuario in db.Usuarios.AsNoTracking()
            join acceso in db.AccesosUsuario.AsNoTracking()
                on usuario.Id equals acceso.UsuarioId
            where usuario.Email.ToLower() == emailNormalizado && usuario.PasswordHash != null && usuario.PasswordHash != "" && acceso.RolId != null && acceso.Activo

            select new UsuarioLogin(
                usuario.Id,
                acceso.Id,
                usuario.Email,
                usuario.PasswordHash!,
                acceso.VersionSesion)
        ).SingleOrDefaultAsync(cancellationToken);
    }
}
