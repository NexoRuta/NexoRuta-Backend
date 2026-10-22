using System.Globalization;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using NexoRuta.Infrastructure.Administracion.Autenticacion;
using NexoRuta.Infrastructure.Outbox;

namespace NexoRuta.Worker.Messaging;

public sealed class KafkaEnvioCreadoPublisher : IDisposable
{
    public const string Topic = PasswordRecoveryMessaging.ShipmentCreatedTopic;
    private readonly Lazy<IProducer<string, string>> producer;
    private readonly ILogger<KafkaEnvioCreadoPublisher> logger;

    public KafkaEnvioCreadoPublisher(IConfiguration configuration, ILogger<KafkaEnvioCreadoPublisher> logger)
    {
        this.logger = logger;
        producer = new Lazy<IProducer<string, string>>(() =>
            new ProducerBuilder<string, string>(CrearConfiguracion(configuration))
                // Native log text can contain endpoint/security information.
                .SetLogHandler((_, _) => { })
                .SetErrorHandler((_, error) => logger.LogWarning(
                    "Kafka producer error. Code: {Code}, Fatal: {Fatal}", error.Code, error.IsFatal))
                .Build());
    }

    internal static ProducerConfig CrearConfiguracion(IConfiguration configuration)
    {
        var servers = configuration[WorkerConfigurationKeys.KafkaBootstrapServers];
        if (string.IsNullOrWhiteSpace(servers))
            throw new InvalidOperationException("Kafka:BootstrapServers is required.");
        var timeout = configuration.GetValue(WorkerConfigurationKeys.KafkaMessageTimeoutMs, 30_000);
        // Leave margin for SQL completion within the existing two-minute lease.
        if (timeout is < 1 or > 60_000)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Kafka timeout must be between 1 and 60000 ms.");
        return new ProducerConfig
        {
            BootstrapServers = servers,
            EnableIdempotence = true,
            Acks = Acks.All,
            MessageTimeoutMs = timeout,
            AllowAutoCreateTopics = false,
            SecurityProtocol = configuration.GetValue<SecurityProtocol?>(WorkerConfigurationKeys.KafkaSecurityProtocol),
            SaslMechanism = configuration.GetValue<SaslMechanism?>(WorkerConfigurationKeys.KafkaSaslMechanism),
            SaslUsername = configuration[WorkerConfigurationKeys.KafkaSaslUsername],
            SaslPassword = configuration[WorkerConfigurationKeys.KafkaSaslPassword],
            SslCaLocation = configuration[WorkerConfigurationKeys.KafkaSslCaLocation]
        };
    }

    internal static Message<string, string> CrearMensaje(OutboxClaim mensaje)
    {
        if (mensaje.Destino != nameof(OutboxDestino.Kafka) || mensaje.Ambito != nameof(OutboxScope.Envio) ||
            mensaje.Tipo != OutboxMessageTypes.ShipmentCreated || mensaje.VersionContrato != OutboxContractVersions.Current ||
            mensaje.OperadorId is null || mensaje.EnvioId is null || mensaje.ClaveAmbito != mensaje.EnvioId)
            throw new ArgumentException("Expected a Kafka EnvioCreado outbox event.", nameof(mensaje));
        var headers = new Headers();
        headers.Add("eventId", Encoding.UTF8.GetBytes(mensaje.Id.ToString("D")));
        headers.Add("eventType", Encoding.UTF8.GetBytes(OutboxMessageTypes.ShipmentCreated));
        headers.Add("eventVersion", Encoding.UTF8.GetBytes(OutboxContractVersions.Current.ToString(CultureInfo.InvariantCulture)));
        headers.Add("occurredAt", Encoding.UTF8.GetBytes(
            mensaje.CreadoEnUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
        headers.Add("operadorId", Encoding.UTF8.GetBytes(mensaje.OperadorId.Value.ToString("D")));
        headers.Add("contentType", Encoding.UTF8.GetBytes("application/json"));
        return new Message<string, string>
        {
            Key = mensaje.EnvioId.Value.ToString("D"), Value = mensaje.Payload, Headers = headers
        };
    }

    internal static Message<string, string> CrearMensaje(OutboxMessage mensaje)
        => CrearMensaje(OutboxClaim.From(mensaje));

    internal static Message<string, string> CreatePasswordRecoveryMessage(OutboxClaim message)
    {
        if (message.Destino != nameof(OutboxDestino.Kafka) || message.Ambito != nameof(OutboxScope.Identidad) ||
            message.Tipo != OutboxMessageTypes.PasswordRecoveryRequested ||
            message.VersionContrato != OutboxContractVersions.Current ||
            message.OperadorId is not null || message.EnvioId is not null || message.EstadoEntrega is not null)
            throw new ArgumentException("Expected a registered PasswordRecoveryRequested identity event.", nameof(message));
        var requested = JsonSerializer.Deserialize<PasswordRecoveryRequestedV1>(message.Payload, PasswordRecoveryJson.Options)
            ?? throw new InvalidDataException("Password recovery event payload is invalid.");
        if (requested.EventId != message.Id || requested.UsuarioId != message.ClaveAmbito ||
            requested.UsuarioId == Guid.Empty || requested.OccurredAt == default ||
            requested.SchemaVersion != PasswordRecoveryEventVersion.Current)
            throw new InvalidDataException("Password recovery event identity is invalid.");
        var headers = new Headers
        {
            { PasswordRecoveryMessaging.EventIdHeader, Encoding.UTF8.GetBytes(requested.EventId.ToString("D")) },
            { PasswordRecoveryMessaging.EventTypeHeader, Encoding.UTF8.GetBytes(OutboxMessageTypes.PasswordRecoveryRequested) },
            { PasswordRecoveryMessaging.EventVersionHeader, Encoding.UTF8.GetBytes(requested.SchemaVersion.ToString(CultureInfo.InvariantCulture)) },
            { PasswordRecoveryMessaging.OccurredAtHeader, Encoding.UTF8.GetBytes(requested.OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)) }
        };
        return new Message<string, string>
        {
            Key = requested.UsuarioId.ToString("D"),
            Value = message.Payload,
            Headers = headers
        };
    }

    public async Task PublicarRecuperacionAsync(OutboxClaim message, CancellationToken cancellationToken = default)
    {
        var recoveryMessage = CreatePasswordRecoveryMessage(message);
        cancellationToken.ThrowIfCancellationRequested();
        var delivery = await producer.Value.ProduceAsync(PasswordRecoveryMessaging.RequestedTopic, recoveryMessage, cancellationToken);
        if (delivery.Status != PersistenceStatus.Persisted)
            throw new KafkaException(new Error(ErrorCode.Local_Fail, "Recovery event was not confirmed persisted."));
        logger.LogInformation("Kafka recovery event published. EventId: {EventId}, Partition: {Partition}, Offset: {Offset}",
            message.Id, delivery.Partition.Value, delivery.Offset.Value);
    }

    public async Task PublicarAsync(OutboxClaim mensaje, CancellationToken cancellationToken = default)
    {
        var contenido = CrearMensaje(mensaje);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var delivery = await producer.Value.ProduceAsync(Topic, contenido, cancellationToken);
            if (delivery.Status != PersistenceStatus.Persisted)
                throw new KafkaException(new Error(ErrorCode.Local_Fail, "Delivery was not confirmed persisted."));
            logger.LogInformation(
                "Kafka delivery confirmed. EventId: {EventId}, Topic: {Topic}, Partition: {Partition}, Offset: {Offset}",
                mensaje.Id, delivery.Topic, delivery.Partition.Value, delivery.Offset.Value);
        }
        catch (ProduceException<string, string> error)
        {
            logger.LogWarning(
                "Kafka delivery failed or uncertain. EventId: {EventId}, Code: {Code}, Status: {Status}, Fatal: {Fatal}",
                mensaje.Id, error.Error.Code, error.DeliveryResult.Status, error.Error.IsFatal);
            throw;
        }
        catch (KafkaException error)
        {
            logger.LogWarning("Kafka publication failed. EventId: {EventId}, Code: {Code}, Fatal: {Fatal}",
                mensaje.Id, error.Error.Code, error.Error.IsFatal);
            throw;
        }
    }

    public void Dispose()
    {
        if (!producer.IsValueCreated) return;
        try
        {
            var remaining = producer.Value.Flush(TimeSpan.FromSeconds(5));
            if (remaining > 0)
                logger.LogWarning("Kafka shutdown has {PendingCount} outstanding deliveries; outbox leases remain recoverable.", remaining);
        }
        catch (KafkaException error)
        {
            logger.LogWarning("Kafka flush failed. Code: {Code}", error.Error.Code);
        }
        finally
        {
            producer.Value.Dispose();
        }
    }
}
