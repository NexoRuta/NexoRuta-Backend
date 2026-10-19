namespace NexoRuta.Application.Administracion.Interfaces;

public interface ICorreoSender
{
    Task EnviarAsync(string destinatario, string asunto, string cuerpoTexto, CancellationToken ct);
}
