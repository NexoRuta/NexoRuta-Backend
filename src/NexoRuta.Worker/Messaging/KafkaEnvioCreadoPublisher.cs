using System.Globalization;
using System.Text;
using Confluent.Kafka;
using NexoRuta.Infrastructure.Outbox;

namespace NexoRuta.Worker.Messaging;

public sealed class KafkaEnvioCreadoPublisher : IDisposable
{
    public const string Topic = "nexoruta.envios.creados.v1";
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
        var servers = configuration["Kafka:BootstrapServers"];
        if (string.IsNullOrWhiteSpace(servers))
            throw new InvalidOperationException("Kafka:BootstrapServers is required.");
        var timeout = configuration.GetValue("Kafka:MessageTimeoutMs", 30_000);
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
            SecurityProtocol = configuration.GetValue<SecurityProtocol?>("Kafka:SecurityProtocol"),
            SaslMechanism = configuration.GetValue<SaslMechanism?>("Kafka:SaslMechanism"),
            SaslUsername = configuration["Kafka:SaslUsername"],
            SaslPassword = configuration["Kafka:SaslPassword"],
            SslCaLocation = configuration["Kafka:SslCaLocation"]
        };
    }

    internal static Message<string, string> CrearMensaje(OutboxClaim mensaje)
    {
        if (mensaje.Destino != nameof(OutboxDestino.Kafka) || mensaje.Tipo != OutboxMessageTypes.ShipmentCreated)
            throw new ArgumentException("Expected a Kafka EnvioCreado outbox event.", nameof(mensaje));
        var headers = new Headers();
        headers.Add("eventId", Encoding.UTF8.GetBytes(mensaje.Id.ToString("D")));
        headers.Add("eventType", Encoding.UTF8.GetBytes(OutboxMessageTypes.ShipmentCreated));
        headers.Add("eventVersion", Encoding.UTF8.GetBytes("1"));
        headers.Add("occurredAt", Encoding.UTF8.GetBytes(
            mensaje.CreadoEnUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
        headers.Add("operadorId", Encoding.UTF8.GetBytes(mensaje.OperadorId.ToString("D")));
        headers.Add("contentType", Encoding.UTF8.GetBytes("application/json"));
        return new Message<string, string>
        {
            Key = mensaje.EnvioId.ToString("D"), Value = mensaje.Payload, Headers = headers
        };
    }

    internal static Message<string, string> CrearMensaje(OutboxMessage mensaje)
        => CrearMensaje(OutboxClaim.From(mensaje));

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
