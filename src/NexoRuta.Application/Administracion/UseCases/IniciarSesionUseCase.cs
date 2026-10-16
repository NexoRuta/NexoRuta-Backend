
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Application.Administracion.Results;

namespace NexoRuta.Application.Administracion.UseCases;

public sealed class IniciarSesionUseCase(
    IUsuariosLoginRepository usuarios,
    IVerificadorPassword verificador)
{
    public async Task<SesionIniciada?> EjecutarAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email) ||
            string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        var usuario = await usuarios.ObtenerPorEmailAsync(
            email.Trim(),
            cancellationToken);

        if (usuario is null)
            return null;

        var passwordValida = verificador.Verificar(
            usuario.Email,
            usuario.PasswordHash,
            password);

        if (!passwordValida)
            return null;

        return new SesionIniciada(
            usuario.UsuarioId,
            usuario.AccesoId,
            usuario.Email,
            usuario.VersionSesion);
    }
}
