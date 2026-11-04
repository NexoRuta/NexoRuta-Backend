using FirebaseAdmin.Messaging;

namespace NexoRuta.Worker.Messaging;

public sealed class TransporteFcmNoSend : ITransporteFcm
{
    public Task<IReadOnlyList<ResultadoEnvioPush>> EnviarAsync(MulticastMessage mensaje, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<ResultadoEnvioPush> resultados = (mensaje.Fids ?? [])
            .Select(_ => new ResultadoEnvioPush(EstadoEnvioPush.Reintentar))
            .ToArray();
        return Task.FromResult(resultados);
    }
}
