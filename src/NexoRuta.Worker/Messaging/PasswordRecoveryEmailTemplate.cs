using System.Globalization;
using System.Text.Encodings.Web;

namespace NexoRuta.Worker.Messaging;

public sealed record PasswordRecoveryEmailContent(string Text, string Html);

public static class PasswordRecoveryEmailTemplate
{
    public static PasswordRecoveryEmailContent Create(string link, DateTimeOffset expiresAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(link);

        var expiry = expiresAtUtc.ToUniversalTime().ToString("dd/MM/yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);
        var encodedLink = HtmlEncoder.Default.Encode(link);
        var text = $"NexoRuta · Portal de comercios\n\nRecibimos una solicitud para crear una contraseña nueva para tu cuenta.\n\nCrear nueva contraseña:\n{link}\n\nEl enlace vence el {expiry}. Si no solicitaste este cambio, ignorá este mensaje.";
        var html = $$"""
            <!doctype html>
            <html lang="es">
            <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"></head>
            <body style="margin:0;padding:24px;background-color:#f3f6fa;color:#17263a;font-family:Arial,Helvetica,sans-serif;line-height:1.5">
              <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="max-width:560px;margin:0 auto;background-color:#ffffff;border:1px solid #d2dce7;border-radius:12px">
                <tr><td style="padding:28px">
                  <p style="margin:0 0 20px;color:#17263a;font-size:18px;font-weight:700">NexoRuta <span style="color:#4c5e74;font-size:14px;font-weight:400">· Portal de comercios</span></p>
                  <h1 style="margin:0 0 12px;font-size:22px;line-height:1.3">Crear una contraseña nueva</h1>
                  <p style="margin:0 0 22px;color:#4c5e74">Recibimos una solicitud para cambiar la contraseña de tu cuenta.</p>
                  <p style="margin:0 0 22px"><a href="{{encodedLink}}" style="display:inline-block;padding:13px 20px;border-radius:8px;background-color:#1d4ed8;color:#ffffff;font-weight:700;text-decoration:none">Crear nueva contraseña</a></p>
                  <p style="margin:0 0 12px;color:#4c5e74">Este enlace vence el <strong>{{expiry}}</strong>.</p>
                  <p style="margin:0;color:#4c5e74;font-size:14px">Si no solicitaste este cambio, ignorá este mensaje.</p>
                </td></tr>
              </table>
            </body>
            </html>
            """;

        return new(text, html);
    }
}
