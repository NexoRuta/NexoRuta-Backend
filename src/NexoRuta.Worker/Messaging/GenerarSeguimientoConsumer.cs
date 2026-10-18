
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NexoRuta.Infrastructure.Outbox;
using NexoRuta.Infrastructure.Seguimiento.Processing;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace NexoRuta.Worker.Messaging;

public sealed class GenerarSeguimientoConsumer(
    IConfiguration configuration,
    IServiceScopeFactory scopeFactory,
    ILogger<GenerarSeguimientoConsumer> logger,
    IHostApplicationLifetime lifetime
) : BackgroundService
{
    private const string HeaderIntentos = "x-nexoruta-retry-count";
    private const string HeaderFallosProcesamiento = "x-nexoruta-processing-failure-count";
    private const int MaximoIntentos = 3;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private sealed record GenerarSeguimientoPayload(
        Guid EnvioId,
        Guid OperadorId);

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        var usuario = configuration["RabbitMq:Username"];
        var password = configuration["RabbitMq:Password"];

        if (string.IsNullOrWhiteSpace(usuario) ||
            string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                "Faltan las credenciales de RabbitMQ.");
        }

        var factory = new ConnectionFactory
        {
            HostName = configuration["RabbitMq:Host"]
                ?? "127.0.0.1",

            Port = int.TryParse(
                configuration["RabbitMq:Port"],
                out var puerto)
                    ? puerto
                    : 5672,

            UserName = usuario,
            Password = password,

            AutomaticRecoveryEnabled = true,
            ConsumerDispatchConcurrency = 1
        };

        await using var connection =
            await factory.CreateConnectionAsync(stoppingToken);

        connection.ConnectionShutdownAsync += (_, cierre) =>
        {
            logger.LogWarning("Conexión RabbitMQ cerrada. Initiator: {Initiator}, Code: {Code}",
                cierre.Initiator, cierre.ReplyCode);
            return Task.CompletedTask;
        };
        connection.RecoverySucceededAsync += (_, _) =>
        {
            logger.LogInformation("Conexión RabbitMQ recuperada; consumidor y topología restaurados.");
            return Task.CompletedTask;
        };
        connection.ConnectionRecoveryErrorAsync += (_, error) =>
        {
            logger.LogError("Recuperación RabbitMQ fallida. ErrorType: {ErrorType}",
                error.Exception.GetType().Name);
            return Task.CompletedTask;
        };

        // Canal para consumir y confirmar mensajes.
        await using var canalConsumo =
            await connection.CreateChannelAsync(
                cancellationToken: stoppingToken);

        // Canal independiente para republicar con confirms.
        var opcionesPublicacion = new CreateChannelOptions(
            publisherConfirmationsEnabled: true,
            publisherConfirmationTrackingEnabled: true);

        await using var canalPublicacion =
            await connection.CreateChannelAsync(
                opcionesPublicacion,
                stoppingToken);

        await RabbitMqSeguimientoTopology.DeclararAsync(
            canalConsumo,
            stoppingToken);

        await canalConsumo.BasicQosAsync(
            prefetchSize: 0,
            prefetchCount: 1,
            global: false,
            cancellationToken: stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(
            canalConsumo);

        consumer.ReceivedAsync += async (_, entrega) =>
        {
            var intentosPrevios = 0;
            var fallosPrevios = 0;
            var contratoValidado = false;
            Guid? idLog = Guid.TryParse(entrega.BasicProperties.MessageId, out var id)
                && id != Guid.Empty ? id : null;

            try
            {
                try
                {
                    intentosPrevios = LeerContador(entrega.BasicProperties, HeaderIntentos);
                    // Legacy messages only counted non-transient processing retries.
                    fallosPrevios = LeerContador(entrega.BasicProperties,
                        HeaderFallosProcesamiento, Math.Min(intentosPrevios, MaximoIntentos));

                    if (!Guid.TryParse(
                            entrega.BasicProperties.MessageId,
                            out var messageId) ||
                        messageId == Guid.Empty)
                    {
                        throw new InvalidDataException(
                            "MessageId inválido.");
                    }

                    if (entrega.BasicProperties.Type != OutboxMessageTypes.TrackingRequested)
                    {
                        throw new InvalidDataException(
                            "Tipo de mensaje incorrecto.");
                    }

                    var payload = JsonSerializer.Deserialize<
                        GenerarSeguimientoPayload>(
                        entrega.Body.Span,
                        JsonOptions);

                    if (payload is null ||
                        payload.EnvioId == Guid.Empty ||
                        payload.OperadorId == Guid.Empty)
                    {
                        throw new InvalidDataException(
                            "Payload de seguimiento inválido.");
                    }

                    contratoValidado = true;

                    await using var scope =
                        scopeFactory.CreateAsyncScope();

                    var procesador = scope.ServiceProvider
                        .GetRequiredService<
                            ProcesarGeneracionSeguimiento>();

                    // Ejecutar y confirmar la transacción SQL.
                    await procesador.EjecutarAsync(
                        messageId,
                        payload.OperadorId,
                        payload.EnvioId,
                        stoppingToken);

                    logger.LogInformation(
                        "Seguimiento persistido. MessageId: {MessageId}",
                        messageId);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    // Sin ACK. Se recuperará al cerrar el canal.
                    return;
                }
                catch (Exception errorProcesamiento)
                {
                    var tipoError = ClasificarError(errorProcesamiento, contratoValidado);
                    var plan = PlanificarFallo(tipoError, intentosPrevios, fallosPrevios);
                    var enviarAFallidos = plan.Fallidos;

                    var destino = enviarAFallidos
                        ? RabbitMqSeguimientoTopology.ColaFallidos
                        : RabbitMqSeguimientoTopology.ColaReintento;

                    // Conservar identidad y contenido del mensaje.
                    var propiedades = new BasicProperties
                    {
                        MessageId =
                            entrega.BasicProperties.MessageId,

                        Type =
                            entrega.BasicProperties.Type,

                        ContentType =
                            entrega.BasicProperties.ContentType
                                ?? "application/json",

                        CorrelationId =
                            entrega.BasicProperties.CorrelationId,

                        Persistent = true,

                        Headers = entrega.BasicProperties.Headers is { } headers
                            ? new Dictionary<string, object?>(headers)
                            : new Dictionary<string, object?>()
                    };

                    propiedades.Headers[HeaderIntentos] = plan.Reintentos;
                    propiedades.Headers[HeaderFallosProcesamiento] = plan.FallosProcesamiento;
                    propiedades.Headers["x-nexoruta-error-kind"] = tipoError;

                    logger.LogWarning(
                        "Trasladando {MessageId} a {Destino}. ErrorKind: {ErrorKind}, " +
                        "ErrorType: {ErrorType}, SqlState: {SqlState}, RetryCount: {RetryCount}, " +
                        "ProcessingFailureCount: {ProcessingFailureCount}, Redelivered: {Redelivered}",
                        idLog, destino, tipoError, errorProcesamiento.GetType().Name,
                        SqlState(errorProcesamiento), plan.Reintentos,
                        plan.FallosProcesamiento, entrega.Redelivered);

                    // Espera el publisher confirm del broker.
                    // Si falla, NO se llega al ACK original.
                    await canalPublicacion.BasicPublishAsync(
                        exchange: string.Empty,
                        routingKey: destino,
                        mandatory: true,
                        basicProperties: propiedades,
                        body: entrega.Body,
                        cancellationToken: stoppingToken);

                    logger.LogInformation("Destino confirmado para {MessageId}: {Destino}",
                        idLog, destino);
                }

                // Se ejecuta únicamente si:
                // - el COMMIT SQL terminó; o
                // - la republicación fue confirmada por RabbitMQ.
                await canalConsumo.BasicAckAsync(
                    deliveryTag: entrega.DeliveryTag,
                    multiple: false,
                    cancellationToken: stoppingToken);

                logger.LogInformation(new EventId(1001, "SeguimientoDeliveryAcknowledged"),
                    "ACK enviado para {MessageId}. DeliveryTag: {DeliveryTag}, Redelivered: {Redelivered}",
                    idLog, entrega.DeliveryTag, entrega.Redelivered);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                // El mensaje queda sin confirmar.
                logger.LogInformation("Cierre sin ACK para {MessageId}; entrega recuperable.", idLog);
            }
            catch (Exception errorInfraestructura)
            {
                logger.LogCritical(
                    "No se pudo confirmar ni trasladar el mensaje " +
                    "{MessageId}. Se detiene el Worker para evitar " +
                    "pérdida de control sobre la entrega. ErrorType: {ErrorType}, " +
                    "Returned: {Returned}, DeliveryTag: {DeliveryTag}",
                    idLog, errorInfraestructura.GetType().Name,
                    errorInfraestructura is PublishException { IsReturn: true }, entrega.DeliveryTag);

                // Cerrar la conexión liberará cualquier entrega
                // que permanezca sin ACK.
                lifetime.StopApplication();
            }
        };

        await canalConsumo.BasicConsumeAsync(
            queue: RabbitMqSeguimientoTopology.ColaPrincipal,
            autoAck: false,
            consumer: consumer,
            cancellationToken: stoppingToken);

        logger.LogInformation(
            "Consumidor de seguimiento iniciado.");

        try
        {
            await Task.Delay(
                Timeout.InfiniteTimeSpan,
                stoppingToken);
        }
        catch (OperationCanceledException)
            when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation(
                "Deteniendo consumidor de seguimiento.");
        }
    }

    internal static string ClasificarError(Exception error, bool contratoValidado)
    {
        if (!contratoValidado && error is InvalidDataException or JsonException)
            return "invalid";

        return EsTransitorio(error) ? "transient" : "processing";
    }

    private static bool EsTransitorio(Exception error) => error switch
    {
        NpgsqlException postgres => postgres.IsTransient,
        TimeoutException => true,
        DbUpdateException { InnerException: { } causa } => EsTransitorio(causa),
        // Npgsql's default EF strategy wraps concrete transient causes in this CLR exception.
        InvalidOperationException { InnerException: { } causa } => EsTransitorio(causa),
        _ => false
    };

    private static string? SqlState(Exception error) => error switch
    {
        PostgresException postgres => postgres.SqlState,
        DbUpdateException { InnerException: { } causa } => SqlState(causa),
        InvalidOperationException { InnerException: { } causa } => SqlState(causa),
        _ => null
    };

    internal static (bool Fallidos, int Reintentos, int FallosProcesamiento) PlanificarFallo(
        string tipoError, int reintentos, int fallosProcesamiento)
    {
        var fallos = tipoError == "processing"
            ? Math.Min(fallosProcesamiento, MaximoIntentos - 1) + 1
            : fallosProcesamiento;
        var fallidos = tipoError == "invalid" ||
            (tipoError == "processing" && fallos >= MaximoIntentos);
        return (fallidos,
            fallidos || reintentos == int.MaxValue ? reintentos : reintentos + 1,
            fallos);
    }

    private static int LeerContador(
        IReadOnlyBasicProperties propiedades, string nombre, int predeterminado = 0)
    {
        if (propiedades.Headers is null ||
            !propiedades.Headers.TryGetValue(
                nombre,
                out var valor))
        {
            return predeterminado;
        }

        return valor switch
        {
            int numero when numero >= 0 => numero,

            long numero
                when numero >= 0 &&
                     numero <= int.MaxValue => (int)numero,

            _ => throw new InvalidDataException(
                "Encabezado de reintentos inválido.")
        };
    }
}
