using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Infrastructure.Administracion.Autenticacion;
using NexoRuta.Infrastructure.Outbox;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace NexoRuta.Worker.Messaging;

public sealed class PasswordRecoveryEmailConsumer(
    IConfiguration configuration,
    IServiceScopeFactory scopeFactory,
    IDataProtectionProvider dataProtection,
    ICorreoSender correo,
    ILogger<PasswordRecoveryEmailConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = RabbitMqWorkerConnection.CreateFactory(configuration);
        await using var connection = await RabbitMqWorkerConnection.ConnectWithRetryAsync(factory, logger, stoppingToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
        await PasswordRecoveryEmailTopology.DeclareAsync(channel, stoppingToken);
        await channel.BasicQosAsync(0, 1, false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) => await ProcessAsync(channel, delivery, stoppingToken);
        await channel.BasicConsumeAsync(PasswordRecoveryMessaging.EmailQueue, autoAck: false, consumer, stoppingToken);
        logger.LogInformation("Password recovery email consumer started.");
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task ProcessAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        var stage = PasswordRecoveryEmailStage.DeliveryStateCheck;
        var messageId = Guid.TryParse(delivery.BasicProperties.MessageId, out var parsed) && parsed != Guid.Empty
            ? parsed : Guid.Empty;
        if (messageId == Guid.Empty)
        {
            logger.LogWarning("Discarding password recovery command without a valid message identifier.");
            await channel.BasicNackAsync(delivery.DeliveryTag, false, false, ct);
            return;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var outbox = scope.ServiceProvider.GetRequiredService<EfOutboxRepository>();
            var state = await outbox.GetEmailDeliveryStateAsync(messageId, ct);
            if (state is nameof(OutboxEmailDeliveryState.Delivered) or nameof(OutboxEmailDeliveryState.Expired) or nameof(OutboxEmailDeliveryState.Failed))
            {
                await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
                return;
            }
            if (state != nameof(OutboxEmailDeliveryState.Pending))
                throw new InvalidDataException("Password recovery delivery state is unavailable.");
            if (delivery.BasicProperties.Type != OutboxMessageTypes.PasswordRecoveryEmail)
                throw new InvalidDataException("Unexpected password recovery command type.");

            stage = PasswordRecoveryEmailStage.Decryption;
            var envelope = JsonSerializer.Deserialize<ProtectedPasswordRecoveryCommand>(delivery.Body.Span, PasswordRecoveryJson.Options)
                ?? throw new InvalidDataException("Password recovery envelope is invalid.");
            var plaintext = dataProtection.CreateProtector(PasswordRecoveryDataProtection.EmailCommandPurpose).Unprotect(envelope.ProtectedCommand);
            var command = JsonSerializer.Deserialize<PasswordRecoveryEmailCommand>(plaintext, PasswordRecoveryJson.Options)
                ?? throw new InvalidDataException("Password recovery command payload is invalid.");
            stage = PasswordRecoveryEmailStage.CommandValidation;
            if (command.MessageId != messageId || string.IsNullOrWhiteSpace(command.Recipient) ||
                string.IsNullOrWhiteSpace(command.Token) || string.IsNullOrWhiteSpace(command.Link))
                throw new InvalidDataException("Password recovery command identity is invalid.");

            if (command.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                stage = PasswordRecoveryEmailStage.DeliveryStatePersistence;
                var markedExpired = await outbox.MarkEmailDeliveryAsync(messageId, OutboxEmailDeliveryState.Expired, ct);
                if (!markedExpired && await outbox.GetEmailDeliveryStateAsync(messageId, ct) != nameof(OutboxEmailDeliveryState.Expired))
                    throw new InvalidOperationException("Expired recovery delivery state was not persisted.");
                await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
                logger.LogInformation("Password recovery email skipped because the link expired. MessageId: {MessageId}", messageId);
                return;
            }

            var email = PasswordRecoveryEmailTemplate.Create(command.Link, command.ExpiresAtUtc);
            stage = PasswordRecoveryEmailStage.SmtpDelivery;
            await correo.EnviarHtmlAsync(
                command.Recipient,
                "Restablecer contraseña de NexoRuta",
                email.Text,
                email.Html,
                null,
                null,
                ct);
            stage = PasswordRecoveryEmailStage.DeliveryStatePersistence;
            var markedDelivered = await outbox.MarkEmailDeliveryAsync(messageId, OutboxEmailDeliveryState.Delivered, ct);
            if (!markedDelivered && await outbox.GetEmailDeliveryStateAsync(messageId, ct) != nameof(OutboxEmailDeliveryState.Delivered))
                throw new InvalidOperationException("Delivered recovery state was not persisted.");
            await channel.BasicAckAsync(delivery.DeliveryTag, false, ct);
            logger.LogInformation("Password recovery email processed. MessageId: {MessageId}", messageId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try { await channel.BasicNackAsync(delivery.DeliveryTag, false, true, CancellationToken.None); }
            catch (Exception error) { logger.LogWarning("Could not requeue a stopping email command. ExceptionType: {ExceptionType}", error.GetType().Name); }
        }
        catch (Exception error)
        {
            logger.LogWarning(
                "Password recovery email command failed at {RecoveryStage}. MessageId: {MessageId}, ExceptionType: {ExceptionType}; retry is broker-controlled.",
                stage, messageId, error.GetType().Name);
            try
            {
                await Task.Delay(PasswordRecoveryMessaging.EmailRetryDelay, ct);
                await channel.BasicRejectAsync(delivery.DeliveryTag, true, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception nackError)
            {
                logger.LogWarning("Password recovery command disposition failed. MessageId: {MessageId}, ExceptionType: {ExceptionType}",
                    messageId, nackError.GetType().Name);
            }
        }
    }

    private enum PasswordRecoveryEmailStage
    {
        DeliveryStateCheck,
        Decryption,
        CommandValidation,
        SmtpDelivery,
        DeliveryStatePersistence
    }
}

public sealed record ProtectedPasswordRecoveryCommand(string ProtectedCommand);
