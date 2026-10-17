using System.Text.Json;
using FirebaseAdmin.Messaging;
using NexoRuta.Infrastructure.Notificaciones;
using NexoRuta.Infrastructure.Outbox;
using NexoRuta.Worker.Messaging;
namespace NexoRuta.Worker;
public sealed class PushOutboxWorker(IServiceScopeFactory scopes, ITransporteFcm transporte, ILogger<PushOutboxWorker> logger) : BackgroundService
{
    private readonly Guid procesador = Guid.NewGuid();
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var outbox = scope.ServiceProvider.GetRequiredService<EfOutboxRepository>();
                var mensaje = await outbox.ReservarSiguienteAsync(procesador, OutboxDestino.PushFcm, stoppingToken);
                if (mensaje is null) { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); continue; }
                try
                {
                    using var limite = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    limite.CancelAfter(TimeSpan.FromSeconds(90)); // Dentro de la reserva compartida de dos minutos.
                    await PublicarAsync(mensaje, scope.ServiceProvider, limite.Token);
                    await outbox.MarcarPublicadoAsync(mensaje.Id, procesador, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    logger.LogWarning("Falló push {MensajeId}. ErrorType: {ErrorType}", mensaje.Id, ex.GetType().Name);
                    var segundos = Math.Min(3600, 5 * Math.Pow(2, Math.Min(mensaje.Intentos, 10))) + Random.Shared.Next(0, 15);
                    await outbox.MarcarErrorAsync(mensaje.Id, procesador, ex.GetType().Name, stoppingToken, TimeSpan.FromSeconds(segundos));
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError("Falló el ciclo push. ErrorType: {ErrorType}", ex.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
    public async Task PublicarAsync(OutboxMessage mensaje, IServiceProvider servicios, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(mensaje.Payload);
        if (mensaje.Tipo != "RepartoModificado" || !json.RootElement.GetProperty("avisoId").TryGetGuid(out var avisoId))
            throw new InvalidDataException("El mensaje no corresponde al contrato de notificación.");
        var repo = servicios.GetRequiredService<EfDespachoPushRepository>();
        var cifrador = servicios.GetRequiredService<CifradorRegistroPush>();
        while (true)
        {
            var lote = await repo.PendientesAsync(mensaje.OperadorId, avisoId, ct);
            if (lote is null || lote.Instalaciones.Count == 0) return;
            if (lote.Aviso.EnvioId != mensaje.EnvioId) throw new InvalidDataException("El aviso no corresponde al envío del outbox.");
            var fids = lote.Instalaciones.Select(cifrador.Descifrar).ToArray();
            var respuesta = await transporte.EnviarAsync(MensajePushReparto.Crear(lote.Aviso, fids, DateTimeOffset.UtcNow), ct);
            if (respuesta.Responses.Count != lote.Instalaciones.Count) throw new InvalidDataException("Firebase no devolvió un resultado por destino.");
            var fallas = false;
            for (var i = 0; i < lote.Instalaciones.Count; i++)
            {
                var envio = respuesta.Responses[i]; var destino = lote.Instalaciones[i];
                if (envio.IsSuccess)
                    await repo.RegistrarAsync(mensaje.OperadorId, avisoId, destino.Id, destino.FidHash, "FcmAceptada", envio.MessageId, ct);
                else if (envio.Exception?.MessagingErrorCode == MessagingErrorCode.Unregistered)
                    await repo.RegistrarAsync(mensaje.OperadorId, avisoId, destino.Id, destino.FidHash, "FidInvalido", null, ct);
                else fallas = true;
            }
            if (fallas) throw new HttpRequestException("Firebase no aceptó todos los destinos; las aceptaciones ya quedaron registradas.");
        }
    }
}
