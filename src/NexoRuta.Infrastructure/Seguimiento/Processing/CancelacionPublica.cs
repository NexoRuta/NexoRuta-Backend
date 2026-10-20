using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Seguimiento.Interfaces;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Seguimiento.Processing;

public sealed class CancelacionPublica(NexoRutaDbContext db, IDataProtectionProvider proteccion) : ICancelacionPublica
{
    public async Task<string> SolicitarCodigoAsync(string token, CancellationToken ct)
    {
        await using var tx = await db.BeginOrJoinAsync(ct);
        var email = await db.Database.SqlQuery<string?>($"SELECT nexoruta_app.public_cancellation_email({token}) AS \"Value\"").SingleAsync(ct);
        if (email is null) { await tx.CommitAsync(ct); return "NoDisponible"; }
        var codigo = RandomNumberGenerator.GetString("ABCDEFGHJKMNPQRSTUVWXYZ23456789", 12);
        var id = Guid.CreateVersion7(); var mensajeId = Guid.CreateVersion7();
        var comando = new CorreoCodigoCancelacion(mensajeId, id, email, codigo);
        var cifrado = proteccion.CreateProtector(CorreoCodigoCancelacion.ProtectionPurpose).Protect(JsonSerializer.Serialize(comando));
        var resultado = await db.Database.SqlQuery<string>($"SELECT nexoruta_app.issue_public_cancel_code({token},{codigo},{id},{mensajeId},{cifrado}) AS \"Value\"").SingleAsync(ct);
        await tx.CommitAsync(ct); return resultado;
    }
    public async Task<string> ConfirmarAsync(string token, string codigo, CancellationToken ct)
    {
        await using var tx = await db.BeginOrJoinAsync(ct);
        var normalizado = codigo.Trim().Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
        var id = Guid.CreateVersion7(); var aviso = Guid.CreateVersion7(); var mensaje = Guid.CreateVersion7();
        var resultado = await db.Database.SqlQuery<string>($"SELECT nexoruta_app.confirm_public_cancellation({token},{normalizado},{id},{aviso},{mensaje}) AS \"Value\"").SingleAsync(ct);
        // Un resultado inválido también se confirma: conserva el contador de intentos.
        await tx.CommitAsync(ct); return resultado;
    }
}
