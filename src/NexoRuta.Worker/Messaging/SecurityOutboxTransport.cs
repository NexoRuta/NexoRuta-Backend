using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using NexoRuta.Infrastructure.Administracion.Autenticacion;
using NexoRuta.Infrastructure.Outbox;
using RabbitMQ.Client;

namespace NexoRuta.Worker.Messaging;

public static class PasswordRecoveryMessaging
{
    public const string EmailQueue = "nexoruta.password-recovery.email.v1";
    public const string EmailDeadLetterQueue = "nexoruta.password-recovery.email.dead.v1";
    public const string RequestedTopic = "nexoruta.password-recovery.requested.v1";
    public const string EventTypeHeader = "eventType";
    public const string EventIdHeader = "eventId";
    public const string EventVersionHeader = "eventVersion";
    public const string OccurredAtHeader = "occurredAt";
    public const int EventVersion = PasswordRecoveryEventVersion.Current;
    public const int MaxEmailRedeliveries = 10;
    public static readonly TimeSpan EmailRetryDelay = TimeSpan.FromSeconds(5);
}

public static class WorkerConfigurationKeys
{
    public const string Transport = "Messaging:Transport";
    public const string RabbitHost = "RabbitMq:Host";
    public const string RabbitPort = "RabbitMq:Port";
    public const string RabbitUsername = "RabbitMq:Username";
    public const string RabbitPassword = "RabbitMq:Password";
    public const string KafkaBootstrapServers = "Kafka:BootstrapServers";
    public const string KafkaSecurityProtocol = "Kafka:SecurityProtocol";
    public const string DataProtectionKeysDirectory = "DataProtection:KeysDirectory";
    public const string SmtpSection = "Email:Smtp";
    public const string MongoConnectionString = "MongoDb:ConnectionString";
    public const string MongoDatabase = "MongoDb:Database";
    public const string MongoCollection = "MongoDb:PasswordRecoveryCollection";
}

public static class WorkerTransportNames
{
    public const string RabbitMq = "RabbitMq";
    public const string Kafka = "Kafka";
    public const string RabbitMqPasswordRecoveryMail = "RabbitMqPasswordRecoveryMail";
    public const string KafkaPasswordRecoveryMongo = "KafkaPasswordRecoveryMongo";
}

public interface ISecurityOutboxTransport
{
    SecurityOutboxDestination Destination { get; }
    Task PublishAsync(SecurityOutboxClaim message, CancellationToken cancellationToken);
}

public sealed class RabbitMqPasswordRecoveryPublisher(IConfiguration configuration) : ISecurityOutboxTransport
{
    public SecurityOutboxDestination Destination => SecurityOutboxDestination.RabbitMq;

    public async Task PublishAsync(SecurityOutboxClaim message, CancellationToken cancellationToken)
    {
        if (message.Destination != SecurityOutboxDestinations.RabbitMq ||
            message.Type != SecurityOutboxMessageTypes.PasswordRecoveryEmail)
            throw new ArgumentException("Expected an encrypted password recovery email command.", nameof(message));

        var username = configuration[WorkerConfigurationKeys.RabbitUsername];
        var password = configuration[WorkerConfigurationKeys.RabbitPassword];
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("RabbitMQ technical credentials are required.");

        var factory = new ConnectionFactory
        {
            HostName = configuration[WorkerConfigurationKeys.RabbitHost] ?? "127.0.0.1",
            Port = configuration.GetValue(WorkerConfigurationKeys.RabbitPort, 5672),
            UserName = username,
            Password = password
        };
        await using var connection = await factory.CreateConnectionAsync(cancellationToken);
        var options = new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true);
        await using var channel = await connection.CreateChannelAsync(options, cancellationToken);
        await PasswordRecoveryEmailTopology.DeclareAsync(channel, cancellationToken);
        var properties = new BasicProperties
        {
            MessageId = message.Id.ToString("D"),
            Type = SecurityOutboxMessageTypes.PasswordRecoveryEmail,
            ContentType = "application/octet-stream",
            Persistent = true
        };
        var body = Encoding.UTF8.GetBytes(message.Payload);
        await channel.BasicPublishAsync(string.Empty, PasswordRecoveryMessaging.EmailQueue, true, properties, body, cancellationToken);
    }
}

public static class PasswordRecoveryEmailTopology
{
    public static async Task DeclareAsync(IChannel channel, CancellationToken cancellationToken)
    {
        await channel.QueueDeclareAsync(
            PasswordRecoveryMessaging.EmailDeadLetterQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: null, cancellationToken: cancellationToken);
        var arguments = new Dictionary<string, object?>
        {
            ["x-queue-type"] = "quorum",
            ["x-delivery-limit"] = PasswordRecoveryMessaging.MaxEmailRedeliveries,
            ["x-dead-letter-exchange"] = string.Empty,
            ["x-dead-letter-routing-key"] = PasswordRecoveryMessaging.EmailDeadLetterQueue
        };
        await channel.QueueDeclareAsync(
            PasswordRecoveryMessaging.EmailQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: arguments, cancellationToken: cancellationToken);
    }
}

public sealed class KafkaPasswordRecoveryPublisher : ISecurityOutboxTransport, IDisposable
{
    private readonly IProducer<string, string> producer;

    public KafkaPasswordRecoveryPublisher(IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration[WorkerConfigurationKeys.KafkaBootstrapServers]))
            throw new InvalidOperationException("Kafka bootstrap servers are required.");
        producer = new ProducerBuilder<string, string>(KafkaEnvioCreadoPublisher.CrearConfiguracion(configuration))
            .SetLogHandler((_, _) => { })
            .Build();
    }

    public SecurityOutboxDestination Destination => SecurityOutboxDestination.Kafka;

    public async Task PublishAsync(SecurityOutboxClaim message, CancellationToken cancellationToken)
    {
        if (message.Destination != SecurityOutboxDestinations.Kafka ||
            message.Type != SecurityOutboxMessageTypes.PasswordRecoveryRequested)
            throw new ArgumentException("Expected a PasswordRecoveryRequested event.", nameof(message));
        var requested = JsonSerializer.Deserialize<PasswordRecoveryRequestedV1>(message.Payload, PasswordRecoveryJson.Options)
            ?? throw new InvalidOperationException("Password recovery event payload is invalid.");
        if (requested.EventId != message.Id || requested.UsuarioId == Guid.Empty || requested.OccurredAt == default)
            throw new InvalidOperationException("Password recovery event identity is invalid.");

        var headers = new Confluent.Kafka.Headers
        {
            { PasswordRecoveryMessaging.EventIdHeader, Encoding.UTF8.GetBytes(requested.EventId.ToString("D")) },
            { PasswordRecoveryMessaging.EventTypeHeader, Encoding.UTF8.GetBytes(SecurityOutboxMessageTypes.PasswordRecoveryRequested) },
            { PasswordRecoveryMessaging.EventVersionHeader, Encoding.UTF8.GetBytes(PasswordRecoveryMessaging.EventVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)) },
            { PasswordRecoveryMessaging.OccurredAtHeader, Encoding.UTF8.GetBytes(requested.OccurredAt.ToUniversalTime().ToString("O")) }
        };
        var result = await producer.ProduceAsync(PasswordRecoveryMessaging.RequestedTopic,
            new Message<string, string> { Key = requested.EventId.ToString("D"), Value = message.Payload, Headers = headers },
            cancellationToken);
        if (result.Status != PersistenceStatus.Persisted)
            throw new KafkaException(new Error(ErrorCode.Local_Fail, "Recovery event was not confirmed persisted."));
    }

    public void Dispose()
    {
        try { producer.Flush(TimeSpan.FromSeconds(5)); }
        finally { producer.Dispose(); }
    }
}
