
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

        var versionCredencial = await verificador.VerificarYObtenerVersionCredencialAsync(
            usuario.UsuarioId,
            password,
            cancellationToken);

        if (versionCredencial is null)
            return null;

        return new SesionIniciada(
            usuario.UsuarioId,
            usuario.AccesoId,
            usuario.Email,
            usuario.VersionSesion,
            usuario.EsPlataforma,
            usuario.TipoOrganizacion,
            usuario.EstadoOrganizacion == NexoRuta.Domain.Administracion.EstadoOrganizacion.Suspendida,
            versionCredencial.Value);
    }
}
