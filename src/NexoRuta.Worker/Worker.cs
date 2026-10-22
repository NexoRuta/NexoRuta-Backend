
using Microsoft.Extensions.DependencyInjection;
using NexoRuta.Infrastructure.Outbox;
using NexoRuta.Worker.Messaging;

namespace NexoRuta.Worker;

public sealed class Worker(
    IServiceScopeFactory scopeFactory,
    OutboxHandlerRegistry handlers,
    ILogger<Worker> logger) : BackgroundService
{
    private readonly Guid procesadorId = Guid.NewGuid();

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Outbox Worker iniciado. Procesador: {ProcesadorId}",
            procesadorId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope =
                    scopeFactory.CreateAsyncScope();

                var repositorio = scope.ServiceProvider
                    .GetRequiredService<EfOutboxRepository>();

                // 1. Reservar un mensaje pendiente de RabbitMQ.
                var mensaje = await repositorio.ReservarSiguienteAsync(
                    procesadorId,
                    OutboxDestino.RabbitMq,
                    stoppingToken);

                if (mensaje is null)
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(2),
                        stoppingToken);

                    continue;
                }

                logger.LogInformation(
                    "Publicando mensaje {MensajeId}, tipo {Tipo}",
                    mensaje.Id,
                    mensaje.Tipo);

                // 2. Intentar publicar el mensaje.
                try
                {
                    await handlers.HandleAsync(
                        mensaje,
                        stoppingToken);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        "Falló la publicación del mensaje {MensajeId}. ErrorType: {ErrorType}",
                        mensaje.Id, ex.GetType().Name);

                    // 3. Registrar error y programar reintento.
                    var registrado = await repositorio.MarcarErrorAsync(
                        mensaje.Id,
                        procesadorId,
                        ex.GetType().Name,
                        stoppingToken);

                    if (!registrado)
                    {
                        logger.LogWarning(
                            "No se pudo actualizar el error de {MensajeId}; " +
                            "la reserva pudo haber vencido.",
                            mensaje.Id);
                    }

                    continue;
                }

                // 4. RabbitMQ confirmó: marcar como publicado.
                var confirmado = await repositorio.MarcarPublicadoAsync(
                    mensaje.Id,
                    procesadorId,
                    stoppingToken);

                if (confirmado)
                {
                    logger.LogInformation(
                        "Mensaje {MensajeId} publicado correctamente",
                        mensaje.Id);
                }
                else
                {
                    logger.LogWarning(
                        "RabbitMQ recibió {MensajeId}, pero no se pudo " +
                        "marcar como publicado. Podría reenviarse.",
                        mensaje.Id);
                }
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    "Error en el procesamiento del Outbox. ErrorType: {ErrorType}", ex.GetType().Name);

                try
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(5),
                        stoppingToken);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        logger.LogInformation("Outbox Worker detenido.");
    }
}
