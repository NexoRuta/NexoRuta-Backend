namespace NexoRuta.Domain.Reparto;

public sealed class ParadaRuta
{
    public Guid RutaId { get; private set; }
    public Guid OperadorId { get; private set; }
    public Guid EnvioId { get; private set; }
    public int Orden { get; private set; }
    public DateTimeOffset LlegadaEstimadaUtc { get; private set; }
    public bool Activa { get; private set; } = true;
    public string? MotivoSuspension { get; private set; }
    private ParadaRuta() { }
    public ParadaRuta(Guid rutaId, Guid operadorId, Guid envioId, int orden, DateTimeOffset llegadaEstimada)
    {
        if (rutaId == Guid.Empty || operadorId == Guid.Empty || envioId == Guid.Empty || orden < 1 || llegadaEstimada == default)
            throw new ArgumentException("La parada requiere ruta, operador, envío, orden y llegada estimada.");
        RutaId = rutaId; OperadorId = operadorId; EnvioId = envioId; Orden = orden;
        LlegadaEstimadaUtc = llegadaEstimada.ToUniversalTime();
    }
    public void Suspender(string motivo)
    {
        if (string.IsNullOrWhiteSpace(motivo) || motivo.Length > 500) throw new ArgumentException("Indicá el motivo de suspensión de la entrega.");
        Activa = false; MotivoSuspension = motivo.Trim();
    }
    public void Completar() => Activa = false;
}
