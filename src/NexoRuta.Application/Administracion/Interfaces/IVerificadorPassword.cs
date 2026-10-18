
namespace NexoRuta.Application.Administracion.Interfaces;

public interface IVerificadorPassword
{
    Task<bool> VerificarAsync(
        Guid usuarioId,
        string password,
        CancellationToken cancellationToken = default);
}
