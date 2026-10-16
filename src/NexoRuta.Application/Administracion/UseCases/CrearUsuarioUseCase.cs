
using System.ComponentModel.DataAnnotations;
using NexoRuta.Application.Administracion.Dtos;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Domain.Administracion;

namespace NexoRuta.Application.Administracion.UseCases;

public sealed class CrearUsuarioUseCase(
    IUsuarioActual usuarioActual,
    IPermisosUsuarioRepository permisos,
    IUsuariosOrganizacionRepository usuarios,
    IRolesRepository roles,
    IGeneradorPasswordHash generadorHash)
{
    public async Task<UsuarioOrganizacionResumen> EjecutarAsync(
        string email,
        string password,
        Guid rolId,
        CancellationToken ct = default)
    {
        // 1. Autorizar al usuario que realiza la operación.
        var actor = await usuarioActual.ObtenerAsync(ct);

        if (!await permisos.TienePermisoAsync(
            actor.UsuarioId,
            actor.AccesoId,
            "usuarios.gestionar",
            ct))
        {
            throw new AccesoNoPermitidoException();
        }

        // 2. Validar los datos.
        var emailNormalizado = email?.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(emailNormalizado) ||
            emailNormalizado.Length > 254 ||
            !new EmailAddressAttribute().IsValid(emailNormalizado))
        {
            throw new ArgumentException(
                "El correo electrónico no es válido.",
                nameof(email));
        }

        if (string.IsNullOrWhiteSpace(password) ||
            password.Length < 12)
        {
            throw new ArgumentException(
                "La contraseña debe tener al menos 12 caracteres.",
                nameof(password));
        }

        if (rolId == Guid.Empty)
        {
            throw new ArgumentException(
                "El rol es obligatorio.",
                nameof(rolId));
        }

        // 3. Obtener un rol de la propia organización.
        var rol = await roles.ObtenerAsync(actor, rolId, ct);

        if (rol is null ||
            rol.OperadorId != actor.OperadorId ||
            rol.ComercioId != actor.ComercioId)
        {
            throw new AccesoNoPermitidoException();
        }

        if (rol.EsProtegidoSistema && !await permisos.TienePermisoAsync(
            actor.UsuarioId, actor.AccesoId, "configuracion.gestionar", ct))
        {
            throw new AccesoNoPermitidoException();
        }

        // 4. Evitar cuentas duplicadas.
        if (await usuarios.ExisteEmailAsync(emailNormalizado, ct))
        {
            throw new CorreoYaRegistradoException();
        }

        // 5. Crear la identidad y su acceso organizacional.
        var passwordHash = generadorHash.Generar(
            emailNormalizado, password);

        var usuario = new Usuario(
            emailNormalizado, passwordHash);

        var acceso = new AccesoUsuario(
            usuario.Id,
            actor.OperadorId,
            actor.ComercioId,
            rol);

        // 6. Persistir las dos entidades atómicamente.
        await usuarios.GuardarNuevoAsync(
            usuario, acceso, ct);

        return new UsuarioOrganizacionResumen(
            usuario.Id,
            acceso.Id,
            usuario.Email,
            rol.Id,
            rol.Codigo,
            rol.Nombre,
            acceso.Activo);
    }
}
