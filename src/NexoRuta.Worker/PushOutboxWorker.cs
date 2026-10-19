using System.Text.Json;
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
                if (!await ProcesarSiguienteAsync(stoppingToken))
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError("Falló el ciclo push. ErrorType: {ErrorType}", ex.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
    public async Task<bool> ProcesarSiguienteAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<EfOutboxRepository>();
        var mensaje = await outbox.ReservarSiguienteAsync(procesador, OutboxDestino.PushFcm, ct);
        if (mensaje is null) return false;
        try
        {
            using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limite.CancelAfter(TimeSpan.FromSeconds(90));
            await PublicarAsync(mensaje, scope.ServiceProvider, limite.Token);
            if (!await outbox.MarcarPublicadoAsync(mensaje.Id, procesador, ct))
                logger.LogWarning("El resultado push {MensajeId} quedó registrado pero su reserva venció.", mensaje.Id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning("Falló push {MensajeId}. ErrorType: {ErrorType}", mensaje.Id, ex.GetType().Name);
            await outbox.MarcarErrorAsync(mensaje.Id, procesador, ex.GetType().Name, ct);
        }
        return true;
    }

    private async Task PublicarAsync(OutboxClaim mensaje, IServiceProvider servicios, CancellationToken ct)
    {
        if (mensaje.OperadorId is not Guid operadorId || operadorId == Guid.Empty || mensaje.EnvioId is null
            || mensaje.Destino != nameof(OutboxDestino.PushFcm) || mensaje.Ambito != nameof(OutboxScope.Envio)
            || mensaje.ClaveAmbito != mensaje.EnvioId || mensaje.VersionContrato != OutboxContractVersions.Current)
            throw new InvalidDataException("El mensaje no corresponde al ámbito de reparto.");
        using var json = JsonDocument.Parse(mensaje.Payload);
        if (mensaje.Tipo != OutboxMessageTypes.DeliveryChanged || !json.RootElement.GetProperty("avisoId").TryGetGuid(out var avisoId))
            throw new InvalidDataException("El mensaje no corresponde al contrato de notificación.");
        var repo = servicios.GetRequiredService<EfDespachoPushRepository>();
        var cifrador = servicios.GetRequiredService<CifradorRegistroPush>();
        while (true)
        {
            var lote = await repo.PendientesAsync(mensaje.Id, procesador, ct);
            if (lote.Aviso.Id != avisoId || lote.Aviso.OperadorId != operadorId || lote.Aviso.EnvioId != mensaje.EnvioId)
                throw new InvalidDataException("El aviso no corresponde al envío del outbox.");
            if (lote.Instalaciones.Count == 0) return;
            var fids = lote.Instalaciones.Select(cifrador.Descifrar).ToArray();
            var respuesta = await transporte.EnviarAsync(MensajePushReparto.Crear(lote.Aviso, fids, DateTimeOffset.UtcNow), ct);
            if (respuesta.Count != lote.Instalaciones.Count) throw new InvalidDataException("Firebase no devolvió un resultado por destino.");
            var fallas = false;
            for (var i = 0; i < lote.Instalaciones.Count; i++)
            {
                var envio = respuesta[i]; var destino = lote.Instalaciones[i];
                if (envio.Estado == EstadoEnvioPush.Aceptado)
                    await repo.RegistrarAsync(mensaje.Id, procesador, destino.Id, destino.FidHash, "FcmAceptada", envio.IdProveedor, ct);
                else if (envio.Estado == EstadoEnvioPush.RegistroInvalido)
                    await repo.RegistrarAsync(mensaje.Id, procesador, destino.Id, destino.FidHash, "FidInvalido", null, ct);
                else fallas = true;
            }
            if (fallas) throw new HttpRequestException("Firebase no aceptó todos los destinos; las aceptaciones ya quedaron registradas.");
        }
    }
}
