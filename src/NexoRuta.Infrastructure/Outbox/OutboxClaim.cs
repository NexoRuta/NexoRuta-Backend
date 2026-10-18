namespace NexoRuta.Infrastructure.Outbox;

public sealed record OutboxClaim(
    Guid Id,
    Guid OperadorId,
    Guid EnvioId,
    string Tipo,
    string Destino,
    string Payload,
    int Intentos,
    DateTimeOffset CreadoEnUtc)
{
    public static OutboxClaim From(OutboxMessage message) => new(
        message.Id, message.OperadorId, message.EnvioId, message.Tipo,
        message.Destino.ToString(), message.Payload, message.Intentos, message.CreadoEnUtc);
}
