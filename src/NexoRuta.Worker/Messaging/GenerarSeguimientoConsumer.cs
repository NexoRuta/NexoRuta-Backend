
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NexoRuta.Infrastructure.Seguimiento.Processing;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace NexoRuta.Worker.Messaging;

public sealed class GenerarSeguimientoConsumer(
    IConfiguration configuration,
    IServiceScopeFactory scopeFactory,
    ILogger<GenerarSeguimientoConsumer> logger,
    IHostApplicationLifetime lifetime
) : BackgroundService
{
    private const string HeaderIntentos = "x-nexoruta-retry-count";
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

            try
            {
                try
                {
                    intentosPrevios = LeerIntentos(
                        entrega.BasicProperties);

                    if (!Guid.TryParse(
                            entrega.BasicProperties.MessageId,
                            out var messageId) ||
                        messageId == Guid.Empty)
                    {
                        throw new InvalidDataException(
                            "MessageId inválido.");
                    }

                    if (entrega.BasicProperties.Type !=
                        "GenerarSeguimiento")
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
                    var errorPermanente =
                        errorProcesamiento is InvalidDataException
                            or JsonException;

                    var intentoActual = Math.Min(
                        intentosPrevios + 1,
                        MaximoIntentos);

                    var enviarAFallidos =
                        errorPermanente ||
                        intentoActual >= MaximoIntentos;

                    var destino = enviarAFallidos
                        ? RabbitMqSeguimientoTopology.ColaFallidos
                        : RabbitMqSeguimientoTopology.ColaReintento;

                    // Solo incrementar cuando realmente se programa un reintento.
                    var reintentosAcumulados = enviarAFallidos
                        ? intentosPrevios
                        : intentoActual;

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

                        Headers = new Dictionary<string, object?>
                        {
                            [HeaderIntentos] = reintentosAcumulados,
                            ["x-nexoruta-error-kind"] =
                                errorPermanente
                                    ? "invalid"
                                    : "processing"
                        }
                    };

                    // Espera el publisher confirm del broker.
                    // Si falla, NO se llega al ACK original.
                    await canalPublicacion.BasicPublishAsync(
                        exchange: string.Empty,
                        routingKey: destino,
                        mandatory: true,
                        basicProperties: propiedades,
                        body: entrega.Body,
                        cancellationToken: stoppingToken);

                    logger.LogWarning(
                        errorProcesamiento,
                        "Mensaje {MessageId} enviado a {Destino}. " +
                        "Intento {Intento}/{Maximo}.",
                        entrega.BasicProperties.MessageId,
                        destino,
                        intentoActual,
                        MaximoIntentos);
                }

                // Se ejecuta únicamente si:
                // - el COMMIT SQL terminó; o
                // - la republicación fue confirmada por RabbitMQ.
                await canalConsumo.BasicAckAsync(
                    deliveryTag: entrega.DeliveryTag,
                    multiple: false,
                    cancellationToken: stoppingToken);
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                // El mensaje queda sin confirmar.
            }
            catch (Exception errorInfraestructura)
            {
                logger.LogCritical(
                    errorInfraestructura,
                    "No se pudo confirmar ni trasladar el mensaje " +
                    "{MessageId}. Se detiene el Worker para evitar " +
                    "pérdida de control sobre la entrega.",
                    entrega.BasicProperties.MessageId);

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

    private static int LeerIntentos(
        IReadOnlyBasicProperties propiedades)
    {
        if (propiedades.Headers is null ||
            !propiedades.Headers.TryGetValue(
                HeaderIntentos,
                out var valor))
        {
            return 0;
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
