using Microsoft.AspNetCore.DataProtection;
using NexoRuta.Infrastructure.Administracion.Autenticacion;
using RabbitMQ.Client;

namespace NexoRuta.Worker.Messaging;

public static class PasswordRecoveryMessaging
{
    public const string EmailQueue = "nexoruta.password-recovery.email.v1";
    public const string EmailDeadLetterQueue = "nexoruta.password-recovery.email.dead.v1";
    public const string RequestedTopic = "nexoruta.password-recovery.requested.v1";
    public const string MongoConsumerGroup = "nexoruta.password-recovery.mongo.v1";
    public const string MongoCollection = "PasswordRecoveryRequested";
    public const string ShipmentCreatedTopic = "nexoruta.envios.creados.v1";
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
    public const string KafkaSaslMechanism = "Kafka:SaslMechanism";
    public const string KafkaSaslUsername = "Kafka:SaslUsername";
    public const string KafkaSaslPassword = "Kafka:SaslPassword";
    public const string KafkaSslCaLocation = "Kafka:SslCaLocation";
    public const string KafkaMessageTimeoutMs = "Kafka:MessageTimeoutMs";
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
    public const string Push = "Push";
}

public static class PasswordRecoveryEmailTopology
{
    private const string QueueTypeArgument = "x-queue-type";
    private const string DeliveryLimitArgument = "x-delivery-limit";
    private const string DeadLetterExchangeArgument = "x-dead-letter-exchange";
    private const string DeadLetterRoutingKeyArgument = "x-dead-letter-routing-key";

    public static async Task DeclareAsync(IChannel channel, CancellationToken cancellationToken)
    {
        await channel.QueueDeclareAsync(
            PasswordRecoveryMessaging.EmailDeadLetterQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: null, cancellationToken: cancellationToken);
        var arguments = new Dictionary<string, object?>
        {
            [QueueTypeArgument] = "quorum",
            [DeliveryLimitArgument] = PasswordRecoveryMessaging.MaxEmailRedeliveries,
            [DeadLetterExchangeArgument] = string.Empty,
            [DeadLetterRoutingKeyArgument] = PasswordRecoveryMessaging.EmailDeadLetterQueue
        };
        // RabbitMQ 4.3 quorum queues always support strict message priorities; x-max-priority is classic-only.
        await channel.QueueDeclareAsync(
            PasswordRecoveryMessaging.EmailQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: arguments, cancellationToken: cancellationToken);
    }
}
