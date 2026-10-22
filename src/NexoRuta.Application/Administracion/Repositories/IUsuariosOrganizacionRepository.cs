
using NexoRuta.Application.Administracion.Dtos;
using NexoRuta.Domain.Administracion;
using NexoRuta.Application.Administracion.Context;
namespace NexoRuta.Application.Administracion.Repositories;

public interface IUsuariosOrganizacionRepository
{
    Task<IReadOnlyList<UsuarioOrganizacionResumen>> ListarAsync(
        Guid? operadorId,
        Guid? comercioId,
        CancellationToken cancellationToken = default);

    Task<bool> ExisteEmailAsync(
        string email,
        CancellationToken cancellationToken = default);

    Task GuardarNuevoAsync(
        Usuario usuario,
        string password,
        AccesoUsuario acceso,
        CancellationToken cancellationToken = default);
    Task<bool> CambiarEstadoAsync(
        ContextoUsuario actor,
        Guid usuarioId,
        bool activo,
        CancellationToken cancellationToken = default);

    Task<bool> CambiarEmailAsync(
        ContextoUsuario actor, Guid usuarioId, string emailNormalizado,
        long version, CancellationToken cancellationToken = default);
}
