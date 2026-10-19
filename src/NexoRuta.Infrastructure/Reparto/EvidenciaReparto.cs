namespace NexoRuta.Infrastructure.Reparto;
public sealed class EvidenciaReparto
{
    public Guid Id { get; init; }
    public Guid OperadorId { get; init; }
    public Guid AccesoId { get; init; }
    public Guid RutaId { get; init; }
    public Guid EnvioId { get; init; }
    public string Tipo { get; init; } = "";
    public string Hash { get; init; } = "";
    public byte[] Cifrado { get; init; } = [];
    public byte[] Nonce { get; init; } = [];
    public byte[] Tag { get; init; } = [];
    public DateTimeOffset RecibidaEnUtc { get; init; }
}
