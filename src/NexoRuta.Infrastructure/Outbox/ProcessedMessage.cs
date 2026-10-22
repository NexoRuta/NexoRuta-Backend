namespace NexoRuta.Infrastructure.Outbox;

public sealed class ProcessedMessage
{
    public Guid MessageId { get; private set; }

    public string Consumer { get; private set; } = string.Empty;

    public DateTimeOffset ProcessedAtUtc { get; private set; }

    private ProcessedMessage() { }

    public ProcessedMessage(Guid messageId, string consumer)
    {
        if (messageId == Guid.Empty)
            throw new ArgumentException(
                "El identificador del mensaje es obligatorio.",
                nameof(messageId));

        if (string.IsNullOrWhiteSpace(consumer))
            throw new ArgumentException(
                "El consumidor es obligatorio.",
                nameof(consumer));

        MessageId = messageId;
        Consumer = consumer.Trim();
        ProcessedAtUtc = DateTimeOffset.UtcNow;
    }
}