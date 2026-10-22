namespace NexoRuta.Application.Administracion.Interfaces;

public interface ITokensRenovacion
{
    (string Token, string Hash) Crear();
    string? CalcularHash(string token);
}
