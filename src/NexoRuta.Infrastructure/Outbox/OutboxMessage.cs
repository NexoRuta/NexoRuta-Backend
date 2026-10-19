using System.Text.Json;

namespace NexoRuta.Infrastructure.Outbox;

public static class OutboxMessageTypes
{
    public const string TrackingRequested = "GenerarSeguimiento";
    public const string ShipmentCreated = "EnvioCreado";
    public const string PasswordRecoveryEmail = "PasswordRecoveryEmail";
    public const string PasswordRecoveryRequested = "PasswordRecoveryRequested";
}

public static class OutboxContractVersions
{
    public const int Current = 1;
}

public enum OutboxDestino { RabbitMq, Kafka }
public enum OutboxScope { Envio, Identidad }
public enum OutboxPriority { Baja = 0, Normal = 1, Alta = 2 }
public enum OutboxEstado { Pendiente, Procesando, Publicado, Error }
public enum OutboxEmailDeliveryState { Pending, Delivered, Expired, Failed }

public sealed class OutboxMessage
{
    public Guid Id { get; private set; }
    public Guid? OperadorId { get; private set; }
    public Guid? EnvioId { get; private set; }
    public OutboxScope Ambito { get; private set; }
    public Guid ClaveAmbito { get; private set; }
    public Guid? ProcesadorId { get; private set; }
    public string Tipo { get; private set; } = string.Empty;
    public int VersionContrato { get; private set; }
    public OutboxDestino Destino { get; private set; }
    public OutboxPriority Prioridad { get; private set; }
    public OutboxEstado Estado { get; private set; }
    public string Payload { get; private set; } = string.Empty;
    public OutboxEmailDeliveryState? EstadoEntrega { get; private set; }
    public int Intentos { get; private set; }
    public DateTimeOffset CreadoEnUtc { get; private set; }
    public DateTimeOffset ProximoIntentoEnUtc { get; private set; }
    public DateTimeOffset? PublicadoEnUtc { get; private set; }
    public DateTimeOffset? ReservaHastaUtc { get; private set; }
    public string? UltimoError { get; private set; }

    private OutboxMessage() { }

    public static OutboxMessage ForShipment(
        Guid operatorId, Guid shipmentId, string type, OutboxDestino destination, string payload,
        int contractVersion = OutboxContractVersions.Current, Guid? id = null)
    {
        var priority = (type, destination, contractVersion) switch
        {
            (OutboxMessageTypes.TrackingRequested, OutboxDestino.RabbitMq, OutboxContractVersions.Current) => OutboxPriority.Alta,
            (OutboxMessageTypes.ShipmentCreated, OutboxDestino.Kafka, OutboxContractVersions.Current) => OutboxPriority.Normal,
            _ => throw new ArgumentException("Unregistered shipment outbox contract.")
        };
        return Create(id ?? Guid.CreateVersion7(), operatorId, shipmentId, OutboxScope.Envio, shipmentId,
            type, contractVersion, destination, priority, payload, null);
    }

    public static OutboxMessage ForIdentity(
        Guid userId, string type, OutboxDestino destination, string payload,
        int contractVersion = OutboxContractVersions.Current, Guid? id = null)
    {
        var (priority, deliveryState) = (type, destination, contractVersion) switch
        {
            (OutboxMessageTypes.PasswordRecoveryEmail, OutboxDestino.RabbitMq, OutboxContractVersions.Current)
                => (OutboxPriority.Alta, (OutboxEmailDeliveryState?)OutboxEmailDeliveryState.Pending),
            (OutboxMessageTypes.PasswordRecoveryRequested, OutboxDestino.Kafka, OutboxContractVersions.Current)
                => (OutboxPriority.Baja, (OutboxEmailDeliveryState?)null),
            _ => throw new ArgumentException("Unregistered identity outbox contract.")
        };
        return Create(id ?? Guid.CreateVersion7(), null, null, OutboxScope.Identidad, userId,
            type, contractVersion, destination, priority, payload, deliveryState);
    }

    private static OutboxMessage Create(
        Guid id, Guid? operatorId, Guid? shipmentId, OutboxScope scope, Guid scopeKey,
        string type, int contractVersion, OutboxDestino destination, OutboxPriority priority,
        string payload, OutboxEmailDeliveryState? deliveryState)
    {
        if (id == Guid.Empty || scopeKey == Guid.Empty ||
            (operatorId == Guid.Empty) || (shipmentId == Guid.Empty))
            throw new ArgumentException("Outbox identity keys must be non-empty.");
        if (string.IsNullOrWhiteSpace(payload)) throw new ArgumentException("Outbox payload is required.", nameof(payload));
        using var json = JsonDocument.Parse(payload);
        if (json.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Outbox payload must be a JSON object.", nameof(payload));

        var now = DateTimeOffset.UtcNow;
        return new OutboxMessage
        {
            Id = id,
            OperadorId = operatorId,
            EnvioId = shipmentId,
            Ambito = scope,
            ClaveAmbito = scopeKey,
            Tipo = type,
            VersionContrato = contractVersion,
            Destino = destination,
            Prioridad = priority,
            Payload = payload,
            EstadoEntrega = deliveryState,
            Estado = OutboxEstado.Pendiente,
            Intentos = 0,
            CreadoEnUtc = now,
            ProximoIntentoEnUtc = now
        };
    }
}
