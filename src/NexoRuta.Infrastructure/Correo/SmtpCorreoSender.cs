using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;
using NexoRuta.Application.Administracion.Interfaces;

namespace NexoRuta.Infrastructure.Correo;

public sealed class SmtpCorreoSender(IOptions<ConfiguracionSmtp> options) : ICorreoSender
{
    public async Task EnviarAsync(string destinatario, string asunto, string cuerpoTexto, CancellationToken ct)
    {
        var config = options.Value;
        using var message = new MailMessage
        {
            From = new MailAddress(config.FromAddress, config.FromName),
            Subject = asunto,
            Body = cuerpoTexto,
            IsBodyHtml = false
        };
        message.To.Add(new MailAddress(destinatario));

        using var smtp = new SmtpClient(config.Host, config.Port)
        {
            EnableSsl = config.UseSsl,
            Timeout = 10_000
        };
        if (!string.IsNullOrWhiteSpace(config.Username))
            smtp.Credentials = new NetworkCredential(config.Username, config.Password);

        await smtp.SendMailAsync(message, ct);
    }
}
