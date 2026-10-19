using FirebaseAdmin.Messaging;
namespace NexoRuta.Worker.Messaging;
public interface ITransporteFcm
{
    Task<IReadOnlyList<ResultadoEnvioPush>> EnviarAsync(MulticastMessage mensaje, CancellationToken ct);
}
public enum EstadoEnvioPush { Aceptado, RegistroInvalido, Reintentar }
public sealed record ResultadoEnvioPush(EstadoEnvioPush Estado, string? IdProveedor = null);
