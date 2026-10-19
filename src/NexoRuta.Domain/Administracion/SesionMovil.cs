namespace NexoRuta.Domain.Administracion;

public sealed class SesionMovil
{
    public Guid Id { get; private set; }
    public Guid UsuarioId { get; private set; }
    public Guid AccesoId { get; private set; }
    public long VersionSesion { get; private set; }
    public long VersionCredencial { get; private set; }
    public DateTimeOffset CreadaEnUtc { get; private set; }
    public DateTimeOffset ExpiraEnUtc { get; private set; }
    public DateTimeOffset? RevocadaEnUtc { get; private set; }

    private SesionMovil() { }

    public SesionMovil(Guid usuarioId, Guid accesoId, long versionSesion, long versionCredencial,
        DateTimeOffset ahora, DateTimeOffset expiracion)
    {
        ahora = UtcEnMicrosegundos(ahora);
        expiracion = UtcEnMicrosegundos(expiracion);
        if (usuarioId == Guid.Empty || accesoId == Guid.Empty || versionSesion < 0 || versionCredencial < 0)
            throw new ArgumentException("La sesión requiere una identidad y un acceso válidos.");
        if (expiracion <= ahora) throw new ArgumentException("La sesión debe tener vigencia positiva.");
        Id = Guid.CreateVersion7();
        UsuarioId = usuarioId;
        AccesoId = accesoId;
        VersionSesion = versionSesion;
        VersionCredencial = versionCredencial;
        CreadaEnUtc = ahora.ToUniversalTime();
        ExpiraEnUtc = expiracion.ToUniversalTime();
    }

    public bool EstaVigente(DateTimeOffset ahora) => RevocadaEnUtc is null && ahora < ExpiraEnUtc;

    public bool AutorizarRenovacion(bool tokenConsumido, DateTimeOffset ahora)
    {
        if (tokenConsumido) Revocar(ahora);
        return !tokenConsumido && EstaVigente(ahora);
    }

    public void Revocar(DateTimeOffset ahora) => RevocadaEnUtc ??= ahora.ToUniversalTime();

    // El contrato de sesión conserva exactamente los mismos instantes al persistir y renovar.
    private static DateTimeOffset UtcEnMicrosegundos(DateTimeOffset valor)
        => new(valor.UtcTicks - valor.UtcTicks % 10, TimeSpan.Zero);
}
