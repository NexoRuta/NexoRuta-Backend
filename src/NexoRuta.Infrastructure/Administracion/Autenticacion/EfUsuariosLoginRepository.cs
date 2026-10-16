
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Administracion.Dtos;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Administracion.Autenticacion;

public sealed class EfUsuariosLoginRepository(
    NexoRutaDbContext db) : IUsuariosLoginRepository
{
    public async Task<UsuarioLogin?> ObtenerPorEmailAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        var emailNormalizado = email.Trim().ToLowerInvariant();

        var organization = await (
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
        if (organization is not null) return organization;
        return await (from user in db.Usuarios.AsNoTracking()
                      join access in db.AccesosPlataforma on user.Id equals access.UsuarioId
                      join role in db.RolesPlataforma on access.RolId equals role.Id
                      where user.Email.ToLower() == emailNormalizado && user.PasswordHash != null && user.PasswordHash != "" && access.Activo
                      select new UsuarioLogin(user.Id, access.AccesoId, user.Email, user.PasswordHash!, access.VersionSesion, true))
            .SingleOrDefaultAsync(cancellationToken);
    }
}
