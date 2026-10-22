using NexoRuta.Application.Administracion.Dtos;

namespace NexoRuta.Application.Administracion.Repositories;

public interface IUsuariosLoginRepository
{
    Task<UsuarioLogin?> ObtenerPorEmailAsync(
        string email,
        CancellationToken cancellationToken = default);
}
