using System.Security.Cryptography;
using System.Net.Mail;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Results;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Administracion.Autenticacion;

public sealed class ServicioRecuperacionPassword(
    UserManager<Usuario> users,
    NexoRutaDbContext db,
    ICorreoSender correo,
    IOptions<PasswordRecoveryOptions> options,
    ILogger<ServicioRecuperacionPassword> logger) : IRecuperacionPassword
{
    public async Task SolicitarAsync(string email, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is null || string.IsNullOrWhiteSpace(user.Email)) return;

        try
        {
            var token = await users.GeneratePasswordResetTokenAsync(user);
            var url = $"{options.Value.ResetPageUrl}?userId={Uri.EscapeDataString(user.Id.ToString("D"))}&token={Uri.EscapeDataString(token)}";
            await correo.EnviarAsync(user.Email, "Restablecer contraseña de NexoRuta",
                $"Para establecer una contraseña nueva, abrí este enlace dentro de {TimeSpan.FromSeconds(options.Value.TokenLifespanSeconds).TotalMinutes:0} minutos:\n\n{url}\n\nSi no solicitaste este cambio, ignorá este mensaje.", ct);
        }
        catch (Exception error) when (!ct.IsCancellationRequested &&
                                      error is SmtpException or InvalidOperationException or CryptographicException or TimeoutException)
        {
            // Do not expose account existence or log the recipient, message, token, SMTP response, or credentials.
            logger.LogWarning("Password recovery email delivery failed.");
        }
    }

    public async Task<ResultadoRestablecimientoPassword> RestablecerAsync(
        Guid usuarioId, string token, string nuevaPassword, CancellationToken ct)
    {
        if (usuarioId == Guid.Empty || string.IsNullOrEmpty(token) || string.IsNullOrEmpty(nuevaPassword))
            return ResultadoRestablecimientoPassword.EnlaceInvalido;

        var user = await users.FindByIdAsync(usuarioId.ToString("D"));
        if (user is null) return ResultadoRestablecimientoPassword.EnlaceInvalido;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var result = await users.ResetPasswordAsync(user, token, nuevaPassword);
        if (!result.Succeeded)
        {
            await transaction.RollbackAsync(ct);
            return result.Errors.Any(error => error.Code.StartsWith("Password", StringComparison.Ordinal))
                ? ResultadoRestablecimientoPassword.PasswordNoValida
                : ResultadoRestablecimientoPassword.EnlaceInvalido;
        }

        user.RevocarSesionesPorCambioDePassword();
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return ResultadoRestablecimientoPassword.Correcto;
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(ct);
            return ResultadoRestablecimientoPassword.EnlaceInvalido;
        }
    }
}
