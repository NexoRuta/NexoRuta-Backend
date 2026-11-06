using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Seguimiento.Dtos;
using NexoRuta.Application.Seguimiento.Services;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.Infrastructure.Seguimiento.Persistence;
using NexoRuta.Infrastructure.Seguimiento.Security;

namespace NexoRuta.Worker.Messaging;

public sealed class RecipientStateEmailProcessor(NexoRutaDbContext db, IConfiguration configuration,
    CifradorTokenSeguimiento cipher, ICorreoSender correo)
{
    private sealed record Prepared(Guid Id, Guid OperadorId, Guid EnvioId, string Estado, string Destinatario,
        string? TokenHash, byte[]? TokenCifrado, byte[]? Nonce, byte[]? Tag, IdentidadVisualPublica Identidad);

    public async Task EjecutarAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.BeginOrJoinAsync(ct);
        var json = await db.Database.SqlQuery<string?>($"SELECT nexoruta_app.prepare_recipient_state_email({id}) AS \"Value\"").SingleAsync(ct);
        if (json is null) { await tx.CommitAsync(ct); return; }
        var notice = JsonSerializer.Deserialize<Prepared>(json, OperationalConsumer.Json) ?? throw new InvalidDataException("Missing recipient notification.");
        if (notice.TokenHash is null || notice.TokenCifrado is null || notice.Nonce is null || notice.Tag is null)
            throw new InvalidOperationException("The public tracking link is not available yet.");
        var delivery = new EntregaSeguimiento(notice.OperadorId, notice.EnvioId, notice.TokenHash, notice.TokenCifrado, notice.Nonce, notice.Tag);
        var token = cipher.Descifrar(delivery);
        if (GeneradorTokenSeguimiento.CalcularHash(token) != notice.TokenHash) throw new InvalidDataException("Tracking link integrity mismatch.");
        if (!Uri.TryCreate(configuration["Tracking:PublicPortalBaseUrl"], UriKind.Absolute, out var portal)
            || portal.Scheme is not ("http" or "https") || portal.UserInfo.Length != 0 || portal.Query.Length != 0 || portal.Fragment.Length != 0)
            throw new InvalidOperationException("Tracking:PublicPortalBaseUrl must be an absolute trusted public portal URL.");
        var url = portal.AbsoluteUri.TrimEnd('/') + "/seguimiento/" + Uri.EscapeDataString(token);
        var name = notice.Identidad.Nombre;
        var state = NombreEstado(notice.Estado);
        var text = $"{name}\n\nSe registró un cambio en tu envío: {state}.\n\nConsultá el estado actual y las opciones disponibles: {url}";
        var brand = WebUtility.HtmlEncode(name);
        var (background, foreground) = ProcesarCorreoCodigoCancelacion.ColoresHeader(notice.Identidad.ColorPrimario);
        var logo = ProcesarCorreoCodigoCancelacion.LogoPermitido(notice.Identidad);
        var html = "<!doctype html><html lang=\"es\"><body style=\"font:16px/1.5 Arial,sans-serif\"><main><header style=\"padding:24px;background:" + background + ";color:" + foreground + "\">"
            + (logo ? "<img src=\"cid:nexoruta-operator-logo\" width=\"120\" alt=\"Logo de " + brand + "\">" : "")
            + "<h1>" + brand + "</h1></header><section style=\"padding:24px\"><p>Se registró un cambio en tu envío: <strong>" + WebUtility.HtmlEncode(state)
            + "</strong>.</p><p><a href=\"" + WebUtility.HtmlEncode(url) + "\">Consultar el estado actual del envío</a></p>"
            + "<p>" + WebUtility.HtmlEncode(notice.Identidad.CorreoPublico) + " " + WebUtility.HtmlEncode(notice.Identidad.TelefonoPublico) + "</p></section></main></body></html>";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await correo.EnviarHtmlAsync(notice.Destinatario, $"{name}: {state}", text, html,
            logo ? notice.Identidad.LogoContenido : null, logo ? notice.Identidad.LogoTipoContenido : null, timeout.Token);
        if (!await db.Database.SqlQuery<bool>($"SELECT nexoruta_app.mark_recipient_state_email({id}) AS \"Value\"").SingleAsync(ct))
            throw new InvalidOperationException("Recipient notice acknowledgement was not persisted.");
        await tx.CommitAsync(ct);
    }

    internal static string NombreEstado(string value) => value switch
    {
        "Admitido" => "envío admitido",
        "EnDeposito" => "recibido en depósito",
        "EnTransito" => "en camino",
        "Entregado" => "entrega confirmada",
        "EntregadoParcialmente" => "entrega parcial registrada",
        "EntregaPendienteValidacion" => "entrega registrada, pendiente de validación",
        "EntregaSinValidacion" => "entrega registrada sin validación completa",
        "NoEntregado" => "intento de entrega sin completar",
        "Reprogramado" => "nueva visita aprobada",
        "Cancelado" => "envío cancelado",
        "Extraviado" => "envío sin localizar",
        "EnDevolucion" => "devolución iniciada",
        "Devuelto" => "devolución completada",
        "RetiroSolicitado" => "retiro solicitado",
        "RetiroAsignado" => "retiro asignado",
        "Retirado" => "retiro registrado",
        "RetiroFallido" => "retiro sin completar",
        "RetiroEnDeposito" => "retiro recibido en depósito",
        "EntregadoComercio" => "retiro entregado al comercio",
        _ => throw new InvalidDataException("Unregistered recipient transition.")
    };
}

public sealed class RecipientStateOutboxHandler(RabbitMqOutboxPublisher publisher) : IOutboxMessageHandler
{
    public OutboxHandlerRoute Route { get; } = new(NexoRuta.Infrastructure.Outbox.OutboxMessageTypes.RecipientStateEmail, 1, NexoRuta.Infrastructure.Outbox.OutboxDestino.RabbitMq);
    public Task HandleAsync(NexoRuta.Infrastructure.Outbox.OutboxClaim message, CancellationToken ct) => publisher.PublishRecipientStateAsync(message, ct);
}
