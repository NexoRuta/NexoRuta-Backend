using NexoRuta.Application.Administracion.Context;
namespace NexoRuta.Application.Administracion.Interfaces;

public interface IUsuarioActual
{
    Task<ContextoUsuario> ObtenerAsync(CancellationToken cancellationToken = default);
}
