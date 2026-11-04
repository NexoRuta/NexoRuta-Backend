namespace NexoRuta.Domain.Reparto;

public sealed class ConfirmacionExtravioEnvio
{
    public Guid Id { get; private set; }
    public Guid OperadorId { get; private set; }
    public Guid EnvioId { get; private set; }
    public Guid VersionReglasId { get; private set; }
    public Guid ComandoId { get; private set; }
    public long VersionAnterior { get; private set; }
    public long VersionNueva { get; private set; }
    public string Motivo { get; private set; } = "";
    public Guid UsuarioId { get; private set; }
    public Guid AccesoId { get; private set; }
    public Guid OrganizacionId { get; private set; }
    public DateTimeOffset ConfirmadaEnUtc { get; private set; }

    private ConfirmacionExtravioEnvio() { }

    public ConfirmacionExtravioEnvio(Guid operadorId, Guid envioId, Guid versionReglasId, Guid comandoId,
        long versionAnterior, string motivo, Guid usuarioId, Guid accesoId, DateTimeOffset confirmadaEnUtc)
    {
        var normalizado = motivo?.Trim();
        if (operadorId == Guid.Empty || envioId == Guid.Empty || versionReglasId == Guid.Empty || comandoId == Guid.Empty
            || versionAnterior < 0 || versionAnterior == long.MaxValue
            || string.IsNullOrWhiteSpace(normalizado) || normalizado.Length > 500
            || usuarioId == Guid.Empty || accesoId == Guid.Empty
            || confirmadaEnUtc == default || confirmadaEnUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("La confirmación de extravío requiere envío, comando, versión, motivo y actor válidos.");

        Id = Guid.CreateVersion7();
        OperadorId = operadorId;
        EnvioId = envioId;
        VersionReglasId = versionReglasId;
        ComandoId = comandoId;
        VersionAnterior = versionAnterior;
        VersionNueva = checked(versionAnterior + 1);
        Motivo = normalizado;
        UsuarioId = usuarioId;
        AccesoId = accesoId;
        OrganizacionId = operadorId;
        ConfirmadaEnUtc = confirmadaEnUtc;
    }
}
