
namespace NexoRuta.Application.Administracion.Exceptions;

public sealed class CorreoYaRegistradoException : Exception
{
    public CorreoYaRegistradoException()
        : base("El correo ya está registrado.")
    {
    }
}
