namespace NexoRuta.Infrastructure.Outbox;

public sealed record OutboxClaim(
    Guid Id,
    Guid? OperadorId,
    Guid? EnvioId,
    string Ambito,
    Guid ClaveAmbito,
    string Tipo,
    int VersionContrato,
    string Destino,
    string Prioridad,
    string Payload,
    string? EstadoEntrega,
    int Intentos,
    DateTimeOffset CreadoEnUtc)
{
    public OutboxPriority Priority => Enum.Parse<OutboxPriority>(Prioridad, ignoreCase: false);

    public static OutboxClaim From(OutboxMessage message) => new(
        message.Id, message.OperadorId, message.EnvioId, message.Ambito.ToString(), message.ClaveAmbito,
        message.Tipo, message.VersionContrato, message.Destino.ToString(), message.Prioridad.ToString(),
        message.Payload, message.EstadoEntrega?.ToString(), message.Intentos, message.CreadoEnUtc);
}
