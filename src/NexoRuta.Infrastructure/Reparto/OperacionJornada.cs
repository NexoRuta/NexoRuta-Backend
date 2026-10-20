namespace NexoRuta.Infrastructure.Reparto;
public sealed class OperacionJornada
{
    public Guid Id { get; init; }
    public Guid OperadorId { get; init; }
    public Guid AccesoId { get; init; }
    public Guid RutaId { get; init; }
    public long Secuencia { get; init; }
    public Guid? EnvioId { get; init; }
    public string Tipo { get; init; } = "";
    public string Hash { get; init; } = "";
    public string Payload { get; init; } = "";
    public string Respuesta { get; init; } = "";
    public string Resultado { get; init; } = "";
    public DateTimeOffset RecibidaEnUtc { get; init; }
}
