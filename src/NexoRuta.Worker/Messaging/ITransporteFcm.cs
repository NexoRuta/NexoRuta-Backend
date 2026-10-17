using FirebaseAdmin.Messaging;
namespace NexoRuta.Worker.Messaging;
public interface ITransporteFcm
{
    Task<BatchResponse> EnviarAsync(MulticastMessage mensaje, CancellationToken ct);
}
