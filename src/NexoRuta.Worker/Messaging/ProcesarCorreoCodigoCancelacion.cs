using System.Net.Mail;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.Infrastructure.Seguimiento.Processing;

namespace NexoRuta.Worker.Messaging;

public sealed class ProcesarCorreoCodigoCancelacion(NexoRutaDbContext db, IDataProtectionProvider proteccion, ICorreoSender correo)
{
    public async Task EjecutarAsync(Guid mensajeId, CancellationToken ct)
    {
        var sobre = await db.Database.SqlQuery<string?>($"SELECT nexoruta_app.prepare_public_cancel_email({mensajeId}) AS \"Value\"").SingleAsync(ct);
        if (sobre is null) return; // Consumido, reemplazado, enviado o envío ya entregado.
        var texto = proteccion.CreateProtector(CorreoCodigoCancelacion.ProtectionPurpose).Unprotect(sobre);
        var comando = JsonSerializer.Deserialize<CorreoCodigoCancelacion>(texto)
            ?? throw new InvalidDataException("Invalid cancellation email command.");
        if (comando.MessageId != mensajeId || comando.CodigoId == Guid.Empty ||
            !MailAddress.TryCreate(comando.Destinatario, out _) || comando.Codigo is not { Length: 12 } ||
            comando.Codigo.Any(c => !"ABCDEFGHJKMNPQRSTUVWXYZ23456789".Contains(c)))
            throw new InvalidDataException("Invalid cancellation email identity.");
        var codigo = $"{comando.Codigo[..4]}-{comando.Codigo[4..8]}-{comando.Codigo[8..]}";
        var identity = comando.Identidad;
        var brandName = string.IsNullOrWhiteSpace(identity?.Nombre) ? "NexoRuta" : identity.Nombre.Trim();
        var cuerpo = $"Tu código para cancelar el envío de {brandName} es: {codigo}\n\nIngresalo en el enlace de seguimiento. Solo permite cancelar antes de recibir el paquete. Si ya lo recibiste, corresponde una devolución.\n\nNo compartas este código. Si no solicitaste cancelar, ignorá este correo.";
        if (!string.IsNullOrWhiteSpace(identity?.CorreoPublico)) cuerpo += $"\n\nContacto: {identity.CorreoPublico}";
        if (!string.IsNullOrWhiteSpace(identity?.TelefonoPublico)) cuerpo += $"\nTeléfono: {identity.TelefonoPublico}";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var html = CrearHtml(codigo, identity);
        await correo.EnviarHtmlAsync(comando.Destinatario, "Código de cancelación de NexoRuta", cuerpo, html,
            LogoPermitido(identity) ? identity!.LogoContenido : null,
            LogoPermitido(identity) ? identity!.LogoTipoContenido : null, timeout.Token);
        var registrado = await db.Database.SqlQuery<bool>($"SELECT nexoruta_app.mark_public_cancel_email({mensajeId}) AS \"Value\"").SingleAsync(ct);
        if (!registrado) throw new InvalidOperationException("Cancellation email acknowledgement was not persisted.");
    }

    private static string CrearHtml(string codigo, NexoRuta.Application.Seguimiento.Dtos.IdentidadVisualPublica? identity)
    {
        var brand = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(identity?.Nombre) ? "NexoRuta" : identity.Nombre.Trim());
        var (background, text) = ColoresHeader(identity?.ColorPrimario);
        var logo = LogoPermitido(identity)
            ? "<img src=\"cid:nexoruta-operator-logo\" width=\"120\" alt=\"Logo de " + brand + "\" style=\"display:block;max-width:120px;height:auto;margin-bottom:12px\">"
            : "";
        var contact = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(identity?.CorreoPublico))
        {
            var email = WebUtility.HtmlEncode(identity.CorreoPublico.Trim());
            contact.Append("<p><strong>Correo de contacto:</strong> <a href=\"mailto:").Append(email).Append("\">").Append(email).Append("</a></p>");
        }
        if (!string.IsNullOrWhiteSpace(identity?.TelefonoPublico))
        {
            var phone = WebUtility.HtmlEncode(identity.TelefonoPublico.Trim());
            var href = new string(identity.TelefonoPublico.Where(character => character is >= '0' and <= '9' || character == '+').ToArray());
            contact.Append("<p><strong>Teléfono:</strong> <a href=\"tel:").Append(WebUtility.HtmlEncode(href)).Append("\">").Append(phone).Append("</a></p>");
        }
        return "<!doctype html><html lang=\"es\"><body style=\"margin:0;background:#f5f7fa;color:#17212b;font:16px/1.5 Arial,sans-serif\"><main style=\"max-width:560px;margin:24px auto;background:#fff;border:1px solid #cbd5e1;border-radius:8px;overflow:hidden\"><header style=\"padding:24px;background:" + background + ";color:" + text + "\">" + logo + "<h1 style=\"margin:0;font-size:22px\">" + brand + "</h1></header><section style=\"padding:24px\"><p>Tu código para cancelar el envío es:</p><p style=\"font-size:24px;font-weight:bold;letter-spacing:2px\"><code>" + WebUtility.HtmlEncode(codigo) + "</code></p><p>Ingresalo en el enlace de seguimiento. Solo permite cancelar antes de recibir el paquete. Si ya lo recibiste, corresponde una devolución.</p><p>No compartas este código. Si no solicitaste cancelar, ignorá este correo.</p><hr style=\"border:0;border-top:2px solid #64748b\">" + contact + "</section></main></body></html>";
    }

    private static bool LogoPermitido(NexoRuta.Application.Seguimiento.Dtos.IdentidadVisualPublica? identity)
        => identity?.LogoContenido is { Length: > 0 and <= NexoRuta.Infrastructure.IdentidadVisual.ValidadorIdentidadVisual.MaximoLogoBytes }
            && identity.LogoTipoContenido is "image/png" or "image/jpeg";

    private static (string Background, string Text) ColoresHeader(string? color)
    {
        if (color is null || !System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$"))
            return ("#1D4ED8", "#FFFFFF");
        var r = Convert.ToInt32(color[1..3], 16) / 255d;
        var g = Convert.ToInt32(color[3..5], 16) / 255d;
        var b = Convert.ToInt32(color[5..7], 16) / 255d;
        var luminance = .2126 * Linear(r) + .7152 * Linear(g) + .0722 * Linear(b);
        var blackRatio = (luminance + .05) / .05;
        var whiteRatio = 1.05 / (luminance + .05);
        var foreground = whiteRatio >= blackRatio ? ("#FFFFFF", whiteRatio) : ("#000000", blackRatio);
        return foreground.Item2 >= 4.5 ? (color.ToUpperInvariant(), foreground.Item1) : ("#1D4ED8", "#FFFFFF");

        static double Linear(double value) => value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
    }
}
