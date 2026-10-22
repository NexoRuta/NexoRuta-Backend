using System.Text;
using System.Text.Json;
using NexoRuta.Infrastructure.Outbox;
using RabbitMQ.Client;

namespace NexoRuta.Worker.Messaging;

public sealed class RabbitMqOutboxPublisher(IConfiguration configuration) : IAsyncDisposable
{
    public const string TrackingQueue = "nexoruta.seguimiento.generar";
    private readonly SemaphoreSlim publishLock = new(1, 1);
    private IConnection? connection;
    private IChannel? channel;

    public Task PublishTrackingAsync(OutboxClaim message, CancellationToken cancellationToken)
        => PublishAsync(message, CreateTrackingPublication, cancellationToken);

    public Task PublishPasswordRecoveryEmailAsync(OutboxClaim message, CancellationToken cancellationToken)
        => PublishAsync(message, CreateEmailPublication, cancellationToken);

    public Task PublishCancellationCodeAsync(OutboxClaim message, CancellationToken cancellationToken)
        => PublishAsync(message, CreateCancellationPublication, cancellationToken);

    private static (string Queue, BasicProperties Properties, ReadOnlyMemory<byte> Body) CreateCancellationPublication(OutboxClaim message)
    {
        Validate(message, OutboxDestino.RabbitMq, OutboxScope.Envio, OutboxMessageTypes.CancellationCodeEmail, OutboxContractVersions.Current);
        if (message.OperadorId is null || message.EnvioId is null || message.ClaveAmbito != message.EnvioId)
            throw new InvalidDataException("Invalid cancellation email scope.");
        // El consumidor obtiene el sobre autorizado desde PostgreSQL, nunca desde el cuerpo del broker.
        return (CancellationCodeEmailConsumer.Queue, new BasicProperties {
            MessageId = message.Id.ToString("D"), Type = message.Tipo, Persistent = true, ContentType = "application/json"
        }, "{}"u8.ToArray());
    }

    private async Task PublishAsync(OutboxClaim message,
        Func<OutboxClaim, (string Queue, BasicProperties Properties, ReadOnlyMemory<byte> Body)> createPublication,
        CancellationToken cancellationToken)
    {
        await publishLock.WaitAsync(cancellationToken);
        try
        {
            var publisher = await GetChannelAsync(cancellationToken);
            var (queue, properties, body) = createPublication(message);
            await publisher.BasicPublishAsync(string.Empty, queue, true, properties, body, cancellationToken);
        }
        finally { publishLock.Release(); }
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken ct)
    {
        if (connection is { IsOpen: true } && channel is { IsOpen: true }) return channel;
        if (channel is not null) await channel.DisposeAsync();
        if (connection is not null) await connection.DisposeAsync();

        var factory = RabbitMqWorkerConnection.CreateFactory(configuration);
        connection = await factory.CreateConnectionAsync(ct);
        channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
        await channel.QueueDeclareAsync(TrackingQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: null, cancellationToken: ct);
        await PasswordRecoveryEmailTopology.DeclareAsync(channel, ct);
        await CancellationCodeEmailConsumer.DeclareAsync(channel, ct);
        return channel;
    }

    private static (string Queue, BasicProperties Properties, ReadOnlyMemory<byte> Body) CreateTrackingPublication(OutboxClaim message)
    {
        Validate(message, OutboxDestino.RabbitMq, OutboxScope.Envio, OutboxMessageTypes.TrackingRequested,
            OutboxContractVersions.Current);
        if (message.OperadorId is null || message.EnvioId is null || message.ClaveAmbito != message.EnvioId)
            throw new InvalidDataException("The tracking outbox scope is invalid.");
        return (TrackingQueue, new BasicProperties
        {
            MessageId = message.Id.ToString("D"),
            ContentType = "application/json",
            Type = message.Tipo,
            Persistent = true
            // The tracking queue is classic without x-max-priority; the database scheduler handles priority.
        }, Encoding.UTF8.GetBytes(message.Payload));
    }

    private static (string Queue, BasicProperties Properties, ReadOnlyMemory<byte> Body) CreateEmailPublication(OutboxClaim message)
    {
        Validate(message, OutboxDestino.RabbitMq, OutboxScope.Identidad, OutboxMessageTypes.PasswordRecoveryEmail,
            OutboxContractVersions.Current);
        if (message.OperadorId is not null || message.EnvioId is not null || message.EstadoEntrega != nameof(OutboxEmailDeliveryState.Pending))
            throw new InvalidDataException("The identity email outbox scope is invalid.");
        using var json = JsonDocument.Parse(message.Payload);
        if (json.RootElement.ValueKind != JsonValueKind.Object || json.RootElement.EnumerateObject().Count() != 1 ||
            !json.RootElement.TryGetProperty("protectedCommand", out var protectedCommand) ||
            protectedCommand.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(protectedCommand.GetString()))
            throw new InvalidDataException("The protected email envelope is invalid.");

        var rabbitPriority = message.Priority switch
        {
            OutboxPriority.Baja => RabbitMqMessagePriority.Low,
            OutboxPriority.Normal => RabbitMqMessagePriority.Normal,
            OutboxPriority.Alta => RabbitMqMessagePriority.High,
            _ => throw new InvalidDataException("The outbox priority is not registered.")
        };
        return (PasswordRecoveryMessaging.EmailQueue, new BasicProperties
        {
            MessageId = message.Id.ToString("D"),
            ContentType = "application/json",
            Type = message.Tipo,
            Persistent = true,
            Priority = rabbitPriority
        }, Encoding.UTF8.GetBytes(message.Payload));
    }

    private static void Validate(OutboxClaim message, OutboxDestino destination, OutboxScope scope, string type, int version)
    {
        if (message.Destino != destination.ToString() || message.Ambito != scope.ToString() ||
            message.Tipo != type || message.VersionContrato != version)
            throw new InvalidDataException("The outbox route does not match its registered contract.");
    }

    public async ValueTask DisposeAsync()
    {
        if (channel is not null) await channel.DisposeAsync();
        if (connection is not null) await connection.DisposeAsync();
        publishLock.Dispose();
    }
}

public static class RabbitMqMessagePriority
{
    public const byte Low = 0;
    public const byte Normal = 16;
    public const byte High = 31;
}
