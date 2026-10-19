namespace NexoRuta.Domain.ZonasCobertura;

public sealed class ZonaCoberturaRevision
{
    public Guid OperadorId { get; private set; }
    public Guid ZonaId { get; private set; }
    public int NumeroRevision { get; private set; }
    public long Version { get; private set; }
    public DateTimeOffset RegistradaUtc { get; private set; }
    public Guid UsuarioId { get; private set; }
    public string SnapshotJson { get; private set; } = "{}";

    private ZonaCoberturaRevision() { }

    public ZonaCoberturaRevision(
        Guid operadorId,
        Guid zonaId,
        int numeroRevision,
        long version,
        DateTimeOffset registradaUtc,
        Guid usuarioId,
        string snapshotJson)
    {
        if (operadorId == Guid.Empty || zonaId == Guid.Empty || usuarioId == Guid.Empty)
            throw new ArgumentException("El operador, la zona y el usuario son obligatorios.");
        if (numeroRevision <= 0 || version < 0) throw new ArgumentOutOfRangeException(nameof(numeroRevision));
        if (registradaUtc.Offset != TimeSpan.Zero) throw new ArgumentException("La fecha debe estar en UTC.", nameof(registradaUtc));
        if (string.IsNullOrWhiteSpace(snapshotJson)) throw new ArgumentException("El snapshot es obligatorio.", nameof(snapshotJson));
        OperadorId = operadorId;
        ZonaId = zonaId;
        NumeroRevision = numeroRevision;
        Version = version;
        RegistradaUtc = registradaUtc;
        UsuarioId = usuarioId;
        SnapshotJson = snapshotJson;
    }
}
