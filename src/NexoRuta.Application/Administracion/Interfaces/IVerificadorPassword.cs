
namespace NexoRuta.Application.Administracion.Interfaces;

public interface IVerificadorPassword
{
    Task<long?> VerificarYObtenerVersionCredencialAsync(
        Guid usuarioId,
        string password,
        CancellationToken cancellationToken = default);
}
