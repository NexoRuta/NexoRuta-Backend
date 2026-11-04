namespace NexoRuta.Domain.Reparto;

public sealed class BaseOperativa
{
    public Guid OperadorId { get; private set; }
    public string Direccion { get; private set; } = "";
    public double? Latitud { get; private set; }
    public double? Longitud { get; private set; }
    public DateTimeOffset? ConfirmadaEnUtc { get; private set; }
    public Guid? ConfirmadaPorAccesoId { get; private set; }
    public long Version { get; private set; }
    public int? ServicioPredeterminadoMinutos { get; private set; }
    public int? MargenRetornoMinutos { get; private set; }
    private BaseOperativa() { }
    public BaseOperativa(Guid operador, string direccion, int? servicio, int? margen)
    {
        if (operador == Guid.Empty) throw new ArgumentException("Indicá el operador.");
        Version = 1; OperadorId = operador; Asignar(direccion, servicio, margen);
    }
    public void Actualizar(string direccion, int? servicio, int? margen)
    { Asignar(direccion, servicio, margen); Version = checked(Version + 1); }
    private void Asignar(string direccion, int? servicio, int? margen)
    {
        if (string.IsNullOrWhiteSpace(direccion) || direccion.Trim().Length > 500 || servicio < 0 || margen < 0)
            throw new ArgumentException("Indicá dirección de hasta 500 caracteres y tiempos no negativos en minutos.");
        if (Direccion != direccion.Trim()) { Latitud = null; Longitud = null; ConfirmadaEnUtc = null; ConfirmadaPorAccesoId = null; }
        Direccion = direccion.Trim(); ServicioPredeterminadoMinutos = servicio; MargenRetornoMinutos = margen;
    }
    public void Confirmar(double latitud, double longitud, Guid actor, DateTimeOffset ahora)
    {
        if (!double.IsFinite(latitud) || !double.IsFinite(longitud) || latitud is < -90 or > 90 || longitud is < -180 or > 180
            || actor == Guid.Empty || ahora == default) throw new ArgumentException("Confirmá un punto válido con actor e instante del servidor.");
        Latitud = latitud; Longitud = longitud; ConfirmadaPorAccesoId = actor; ConfirmadaEnUtc = ahora.ToUniversalTime(); Version = checked(Version + 1);
    }
}

public sealed record PlanTemporalRuta(DateTimeOffset? SalidaPlanificadaEnUtc, string ZonaHoraria,
    long? BaseVersion, string? DireccionBase, double? LatitudBase, double? LongitudBase,
    DateTimeOffset? BaseConfirmadaEnUtc, Guid? BaseConfirmadaPorAccesoId,
    int? ServicioPredeterminadoMinutos, int? MargenRetornoMinutos, long VersionRutaAlPlanificar,
    IReadOnlyList<Guid> OrdenEnvios);
