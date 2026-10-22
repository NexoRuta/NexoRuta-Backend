namespace NexoRuta.Application.Administracion.Interfaces;

public interface ICorreoSender
{
    Task EnviarAsync(string destinatario, string asunto, string cuerpoTexto, CancellationToken ct);

    // Existing senders keep a text-only fallback; capable transports can add an accessible HTML alternative.
    Task EnviarHtmlAsync(string destinatario, string asunto, string cuerpoTexto, string cuerpoHtml,
        byte[]? imagenInline, string? tipoImagen, CancellationToken ct)
        => EnviarAsync(destinatario, asunto, cuerpoTexto, ct);
}
