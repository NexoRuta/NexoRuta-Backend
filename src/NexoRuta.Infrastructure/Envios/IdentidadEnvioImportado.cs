namespace NexoRuta.Infrastructure.Envios;

public sealed class IdentidadEnvioImportado
{
    public Guid ComercioId { get; private set; }
    public string ReferenciaExterna { get; private set; } = "";
    public Guid EnvioId { get; private set; }
    public Guid OperadorId { get; private set; }
    public short VersionHuella { get; private set; }
    public byte[] Huella { get; private set; } = [];

    private IdentidadEnvioImportado() { }
}
