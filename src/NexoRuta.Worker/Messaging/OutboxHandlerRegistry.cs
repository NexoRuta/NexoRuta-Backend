using NexoRuta.Infrastructure.Outbox;

namespace NexoRuta.Worker.Messaging;

public readonly record struct OutboxHandlerRoute(string Type, int Version, OutboxDestino Destination);

public interface IOutboxMessageHandler
{
    OutboxHandlerRoute Route { get; }
    Task HandleAsync(OutboxClaim message, CancellationToken cancellationToken);
}

public sealed class OutboxHandlerRegistry
{
    private readonly IReadOnlyDictionary<OutboxHandlerRoute, IOutboxMessageHandler> handlers;

    public OutboxHandlerRegistry(IEnumerable<IOutboxMessageHandler> handlers)
    {
        var registered = new Dictionary<OutboxHandlerRoute, IOutboxMessageHandler>();
        foreach (var handler in handlers)
            if (!registered.TryAdd(handler.Route, handler))
                throw new InvalidOperationException($"Duplicate outbox handler route: {handler.Route}.");
        this.handlers = registered;
    }

    public Task HandleAsync(OutboxClaim message, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<OutboxDestino>(message.Destino, ignoreCase: false, out var destination) || !Enum.IsDefined(destination))
            throw new InvalidDataException("The outbox destination is not registered.");
        var route = new OutboxHandlerRoute(message.Tipo, message.VersionContrato, destination);
        if (!handlers.TryGetValue(route, out var handler))
            throw new InvalidDataException("The outbox contract has no handler registered for this broker.");
        return handler.HandleAsync(message, cancellationToken);
    }
}

public sealed class RabbitTrackingOutboxHandler(RabbitMqOutboxPublisher publisher) : IOutboxMessageHandler
{
    public OutboxHandlerRoute Route { get; } = new(
        OutboxMessageTypes.TrackingRequested, OutboxContractVersions.Current, OutboxDestino.RabbitMq);
    public Task HandleAsync(OutboxClaim message, CancellationToken cancellationToken)
        => publisher.PublishTrackingAsync(message, cancellationToken);
}

public sealed class RabbitPasswordRecoveryEmailOutboxHandler(RabbitMqOutboxPublisher publisher) : IOutboxMessageHandler
{
    public OutboxHandlerRoute Route { get; } = new(
        OutboxMessageTypes.PasswordRecoveryEmail, OutboxContractVersions.Current, OutboxDestino.RabbitMq);
    public Task HandleAsync(OutboxClaim message, CancellationToken cancellationToken)
        => publisher.PublishPasswordRecoveryEmailAsync(message, cancellationToken);
}

public sealed class KafkaShipmentCreatedOutboxHandler(KafkaEnvioCreadoPublisher publisher) : IOutboxMessageHandler
{
    public OutboxHandlerRoute Route { get; } = new(
        OutboxMessageTypes.ShipmentCreated, OutboxContractVersions.Current, OutboxDestino.Kafka);
    public Task HandleAsync(OutboxClaim message, CancellationToken cancellationToken)
        => publisher.PublicarAsync(message, cancellationToken);
}

public sealed class KafkaPasswordRecoveryRequestedOutboxHandler(KafkaEnvioCreadoPublisher publisher) : IOutboxMessageHandler
{
    public OutboxHandlerRoute Route { get; } = new(
        OutboxMessageTypes.PasswordRecoveryRequested, OutboxContractVersions.Current, OutboxDestino.Kafka);
    public Task HandleAsync(OutboxClaim message, CancellationToken cancellationToken)
        => publisher.PublicarRecuperacionAsync(message, cancellationToken);
}

public sealed class RabbitCancellationCodeOutboxHandler(RabbitMqOutboxPublisher publisher) : IOutboxMessageHandler
{
    public OutboxHandlerRoute Route { get; } = new(OutboxMessageTypes.CancellationCodeEmail, OutboxContractVersions.Current, OutboxDestino.RabbitMq);
    public Task HandleAsync(OutboxClaim message, CancellationToken cancellationToken)
        => publisher.PublishCancellationCodeAsync(message, cancellationToken);
}
