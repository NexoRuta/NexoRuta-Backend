
namespace NexoRuta.Application.Administracion.Interfaces;

public interface IGeneradorPasswordHash
{
    string Generar(string email, string password);
}
