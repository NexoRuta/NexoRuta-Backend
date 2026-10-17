
namespace NexoRuta.Application.Administracion.Interfaces;

public interface IVerificadorPassword
{
    bool Verificar(
        string email,
        string passwordHash,
        string password);
}
