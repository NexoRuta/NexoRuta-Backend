using System.Net;
using System.Net.Mail;
using System.Text;
using Microsoft.Extensions.Options;
using NexoRuta.Application.Administracion.Interfaces;

namespace NexoRuta.Infrastructure.Correo;

public sealed class SmtpCorreoSender(IOptions<ConfiguracionSmtp> options) : ICorreoSender
{
    public async Task EnviarAsync(string destinatario, string asunto, string cuerpoTexto, CancellationToken ct)
        => await EnviarCoreAsync(destinatario, asunto, cuerpoTexto, null, null, null, ct);

    public async Task EnviarHtmlAsync(string destinatario, string asunto, string cuerpoTexto, string cuerpoHtml,
        byte[]? imagenInline, string? tipoImagen, CancellationToken ct)
        => await EnviarCoreAsync(destinatario, asunto, cuerpoTexto, cuerpoHtml, imagenInline, tipoImagen, ct);

    private async Task EnviarCoreAsync(string destinatario, string asunto, string cuerpoTexto, string? cuerpoHtml,
        byte[]? imagenInline, string? tipoImagen, CancellationToken ct)
    {
        var config = options.Value;
        using var message = new MailMessage
        {
            From = new MailAddress(config.FromAddress, config.FromName),
            Subject = asunto,
            SubjectEncoding = Encoding.UTF8,
            Body = cuerpoTexto,
            BodyEncoding = Encoding.UTF8,
            IsBodyHtml = false
        };
        message.To.Add(new MailAddress(destinatario));
        MemoryStream? logo = null;
        if (cuerpoHtml is not null)
        {
            var view = AlternateView.CreateAlternateViewFromString(cuerpoHtml, Encoding.UTF8, "text/html");
            if (imagenInline is { Length: > 0 } && tipoImagen is "image/png" or "image/jpeg")
            {
                logo = new MemoryStream(imagenInline, writable: false);
                view.LinkedResources.Add(new LinkedResource(logo, tipoImagen) { ContentId = "nexoruta-operator-logo" });
            }
            message.AlternateViews.Add(view);
        }

        using var smtp = new SmtpClient(config.Host, config.Port)
        {
            EnableSsl = config.UseSsl,
            Timeout = 10_000
        };
        if (!string.IsNullOrWhiteSpace(config.Username))
            smtp.Credentials = new NetworkCredential(config.Username, config.Password);

        try { await smtp.SendMailAsync(message, ct); }
        finally { logo?.Dispose(); }
    }
}
