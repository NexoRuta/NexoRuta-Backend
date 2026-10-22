namespace NexoRuta.Domain.Notificaciones;
public sealed class AvisoReparto
{
    public Guid Id { get; private set; }
    public Guid OperadorId { get; private set; }
    public Guid AccesoId { get; private set; }
    public Guid RutaId { get; private set; }
    public Guid EnvioId { get; private set; }
    public long VersionEnvio { get; private set; }
    public string Tipo { get; private set; } = "";
    public string Motivo { get; private set; } = "";
    public DateTimeOffset CreadoEnUtc { get; private set; }
    public DateTimeOffset? RecibidoEnUtc { get; private set; }
    public DateTimeOffset? AbiertoEnUtc { get; private set; }
    public DateTimeOffset? ConfirmadoEnUtc { get; private set; }
    private AvisoReparto() { }
    public AvisoReparto(Guid operadorId, Guid accesoId, Guid rutaId, Guid envioId, long versionEnvio, string tipo, string motivo, DateTimeOffset ahora)
    {
        if (operadorId == Guid.Empty || accesoId == Guid.Empty || rutaId == Guid.Empty || envioId == Guid.Empty
            || versionEnvio < 0 || tipo is not ("Cancelacion" or "Reasignacion") || string.IsNullOrWhiteSpace(motivo) || motivo.Length > 500 || ahora == default)
            throw new ArgumentException("El aviso requiere destinatario actual, envío, versión, tipo, motivo y fecha.");
        Id = Guid.CreateVersion7(); OperadorId = operadorId; AccesoId = accesoId; RutaId = rutaId; EnvioId = envioId;
        VersionEnvio = versionEnvio; Tipo = tipo; Motivo = motivo.Trim(); CreadoEnUtc = ahora.ToUniversalTime();
    }
    public void Registrar(string tipo, DateTimeOffset ahora)
    {
        if (tipo == "Recibido") RecibidoEnUtc ??= ahora.ToUniversalTime();
        else if (tipo == "Abierto") AbiertoEnUtc ??= ahora.ToUniversalTime();
        else if (tipo == "Confirmado") ConfirmadoEnUtc ??= ahora.ToUniversalTime();
        else throw new ArgumentException("La confirmación del aviso no es válida.");
    }
}
