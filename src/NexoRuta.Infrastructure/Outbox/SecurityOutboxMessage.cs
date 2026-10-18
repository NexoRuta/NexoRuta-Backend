namespace NexoRuta.Infrastructure.Outbox;

public static class SecurityOutboxMessageTypes
{
    public const string PasswordRecoveryEmail = "PasswordRecoveryEmail";
    public const string PasswordRecoveryRequested = "PasswordRecoveryRequested";
}

public static class SecurityOutboxDestinations
{
    public const string RabbitMq = "RabbitMq";
    public const string Kafka = "Kafka";
}

public static class PasswordRecoveryEmailDeliveryStates
{
    public const string Pending = "Pending";
    public const string Delivered = "Delivered";
    public const string Expired = "Expired";
    public const string Failed = "Failed";
}

public static class SecurityOutboxStateNames
{
    public const string Pending = "Pending";
    public const string Processing = "Processing";
    public const string Published = "Published";
    public const string Error = "Error";
}

public enum SecurityOutboxDestination
{
    RabbitMq,
    Kafka
}

public enum SecurityOutboxState
{
    Pending,
    Processing,
    Published,
    Error
}

public enum PasswordRecoveryEmailDeliveryState
{
    Pending,
    Delivered,
    Expired,
    Failed
}

public sealed class SecurityOutboxMessage
{
    public Guid Id { get; private set; }
    public string Type { get; private set; } = string.Empty;
    public SecurityOutboxDestination Destination { get; private set; }
    public SecurityOutboxState State { get; private set; }
    public string Payload { get; private set; } = string.Empty;
    public int Attempts { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset NextAttemptAtUtc { get; private set; }
    public Guid? ProcessorId { get; private set; }
    public DateTimeOffset? LeaseUntilUtc { get; private set; }
    public DateTimeOffset? PublishedAtUtc { get; private set; }
    public string? LastError { get; private set; }
    public PasswordRecoveryEmailDeliveryState? DeliveryState { get; private set; }

    private SecurityOutboxMessage() { }

    public SecurityOutboxMessage(
        string type,
        SecurityOutboxDestination destination,
        string payload,
        PasswordRecoveryEmailDeliveryState? deliveryState = null,
        Guid? id = null)
    {
        if (string.IsNullOrWhiteSpace(payload)) throw new ArgumentException("Payload is required.", nameof(payload));
        if ((type, destination, deliveryState) is not
            (SecurityOutboxMessageTypes.PasswordRecoveryEmail, SecurityOutboxDestination.RabbitMq, PasswordRecoveryEmailDeliveryState.Pending) and not
            (SecurityOutboxMessageTypes.PasswordRecoveryRequested, SecurityOutboxDestination.Kafka, null))
            throw new ArgumentException("Invalid security outbox message contract.");

        Id = id ?? Guid.CreateVersion7();
        Type = type;
        Destination = destination;
        Payload = payload;
        DeliveryState = deliveryState;
        State = SecurityOutboxState.Pending;
        CreatedAtUtc = DateTimeOffset.UtcNow;
        NextAttemptAtUtc = CreatedAtUtc;
    }
}

public sealed record SecurityOutboxClaim(
    Guid Id,
    string Type,
    string Destination,
    string Payload,
    int Attempts,
    DateTimeOffset CreatedAtUtc);
