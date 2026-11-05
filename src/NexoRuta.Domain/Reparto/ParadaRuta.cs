namespace NexoRuta.Domain.Reparto;

public sealed class ParadaRuta
{
    public Guid[] BultoIds { get; private set; } = [];
    public void FijarBultos(IEnumerable<Guid> ids)
    {
        var seleccion = ids.ToArray();
        if (BultoIds.Length != 0 || seleccion.Length == 0 || seleccion.Any(i => i == Guid.Empty) || seleccion.Distinct().Count() != seleccion.Length)
            throw new InvalidOperationException("La parada requiere una selección inicial de bultos distintos.");
        BultoIds = seleccion;
    }
    public Guid RutaId { get; private set; }
    public Guid OperadorId { get; private set; }
    public Guid EnvioId { get; private set; }
    public int Orden { get; private set; }
    public DateTimeOffset LlegadaEstimadaUtc { get; private set; }
    public int? ServicioMinutos { get; private set; }
    public string ResultadoVisita { get; private set; } = "Pendiente";
    public bool Activa { get; private set; } = true;
    public string? MotivoSuspension { get; private set; }
    private ParadaRuta() { }
    public ParadaRuta(Guid rutaId, Guid operadorId, Guid envioId, int orden, DateTimeOffset llegadaEstimada, int? servicioMinutos = null)
    {
        if (rutaId == Guid.Empty || operadorId == Guid.Empty || envioId == Guid.Empty || orden < 1 || llegadaEstimada == default || servicioMinutos < 0)
            throw new ArgumentException("La parada requiere ruta, operador, envío, orden y llegada estimada.");
        RutaId = rutaId; OperadorId = operadorId; EnvioId = envioId; Orden = orden;
        ServicioMinutos = servicioMinutos;
        LlegadaEstimadaUtc = llegadaEstimada.ToUniversalTime();
    }
    public void Suspender(string motivo)
    {
        if (string.IsNullOrWhiteSpace(motivo) || motivo.Length > 500) throw new ArgumentException("Indicá el motivo de suspensión de la entrega.");
        Activa = false; ResultadoVisita = "Suspendida"; MotivoSuspension = motivo.Trim();
    }
    public void Retirar(string motivo) { Suspender(motivo); ResultadoVisita = "Retirada"; }
    public void Completar() { Activa = false; ResultadoVisita = "Completada"; }
}
