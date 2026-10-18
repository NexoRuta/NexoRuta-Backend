using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Results;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Outbox;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Administracion.Autenticacion;

public sealed class ServicioRecuperacionPassword(
    UserManager<Usuario> users,
    NexoRutaDbContext db,
    IDataProtectionProvider dataProtection,
    IOptions<PasswordRecoveryOptions> options,
    ILogger<ServicioRecuperacionPassword> logger) : IRecuperacionPassword
{
    public async Task SolicitarAsync(string email, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is null || string.IsNullOrWhiteSpace(user.Email)) return;

        var correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        var stage = PasswordRecoveryStage.TokenGeneration;
        try
        {
            var token = await users.GeneratePasswordResetTokenAsync(user);
            stage = PasswordRecoveryStage.MessagePreparation;
            var occurredAt = DateTimeOffset.UtcNow;
            var expiresAt = occurredAt.AddSeconds(options.Value.TokenLifespanSeconds);
            var emailId = Guid.CreateVersion7();
            var requestedEventId = Guid.CreateVersion7();
            var url = $"{options.Value.ResetPageUrl}?userId={Uri.EscapeDataString(user.Id.ToString("D"))}&token={Uri.EscapeDataString(token)}";
            var emailCommand = new PasswordRecoveryEmailCommand(emailId, user.Email, token, url, expiresAt);
            var protector = dataProtection.CreateProtector(PasswordRecoveryDataProtection.EmailCommandPurpose);
            var encryptedCommand = protector.Protect(JsonSerializer.Serialize(emailCommand));
            var requestedEvent = new PasswordRecoveryRequestedV1(requestedEventId, user.Id, occurredAt);
            var requestedPayload = JsonSerializer.Serialize(requestedEvent, PasswordRecoveryJson.Options);
            var emailMessage = new SecurityOutboxMessage(
                SecurityOutboxMessageTypes.PasswordRecoveryEmail,
                SecurityOutboxDestination.RabbitMq,
                encryptedCommand,
                PasswordRecoveryEmailDeliveryState.Pending,
                emailId);
            var eventMessage = new SecurityOutboxMessage(
                SecurityOutboxMessageTypes.PasswordRecoveryRequested,
                SecurityOutboxDestination.Kafka,
                requestedPayload,
                id: requestedEventId);

            stage = PasswordRecoveryStage.OutboxPersistence;
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            db.SecurityOutboxMessages.AddRange(emailMessage, eventMessage);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (Exception error) when (!ct.IsCancellationRequested && error is not OperationCanceledException)
        {
            // ponytail: log only safe metadata; exception messages may contain SMTP responses or recovery data.
            logger.LogWarning(
                "Password recovery failed at {RecoveryStage} with {ExceptionType} for correlation {CorrelationId}.",
                stage, error.GetType().Name, correlationId);
        }
    }

    private enum PasswordRecoveryStage
    {
        TokenGeneration,
        MessagePreparation,
        OutboxPersistence
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
