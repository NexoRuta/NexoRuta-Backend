using System.Net.Mail;
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
        var cuerpo = $"Tu código para cancelar el envío en NexoRuta es: {codigo}\n\nIngresalo en el enlace de seguimiento. Solo permite cancelar antes de recibir el paquete. Si ya lo recibiste, corresponde una devolución.\n\nNo compartas este código. Si no solicitaste cancelar, ignorá este correo.";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await correo.EnviarAsync(comando.Destinatario, "Código de cancelación de NexoRuta", cuerpo, timeout.Token);
        var registrado = await db.Database.SqlQuery<bool>($"SELECT nexoruta_app.mark_public_cancel_email({mensajeId}) AS \"Value\"").SingleAsync(ct);
        if (!registrado) throw new InvalidOperationException("Cancellation email acknowledgement was not persisted.");
    }
}
