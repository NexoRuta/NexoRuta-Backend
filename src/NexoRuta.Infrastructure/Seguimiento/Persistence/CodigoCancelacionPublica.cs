namespace NexoRuta.Infrastructure.Seguimiento.Persistence;

public sealed class CodigoCancelacionPublica
{
    public Guid Id { get; init; }
    public Guid OperadorId { get; init; }
    public Guid EnvioId { get; init; }
    public Guid MensajeId { get; init; }
    public string Hash { get; init; } = "";
    public DateTimeOffset CreadoEnUtc { get; init; }
    public bool Activo { get; init; }
    public int Intentos { get; init; }
    public DateTimeOffset? ConsumidoEnUtc { get; init; }
    public DateTimeOffset? CorreoEnviadoEnUtc { get; init; }
}
