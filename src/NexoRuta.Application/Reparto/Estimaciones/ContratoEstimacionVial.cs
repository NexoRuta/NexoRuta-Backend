namespace NexoRuta.Application.Reparto.Estimaciones;

public sealed record PuntoVial(double Latitud, double Longitud)
{
    public bool EsValido => double.IsFinite(Latitud) && double.IsFinite(Longitud)
        && Latitud is >= -90 and <= 90 && Longitud is >= -180 and <= 180;
}

public sealed record ParadaEstimable(Guid EnvioId, int Orden, bool Activa, PuntoVial? Punto,
    bool PuntoConfirmado, int? ServicioMinutos, DateTimeOffset LlegadaManualEnUtc);

// The caller must construct this from its authorized, versioned B3 snapshot, not an untrusted HTTP body.
public sealed record SolicitudEstimacionInicial(Guid OperadorId, Guid RutaId, long VersionRuta, long? VersionBase,
    PuntoVial? Base, bool BaseConfirmada, DateTimeOffset? SalidaPlanificadaEnUtc, string ZonaHoraria,
    string? PerfilVial, int? MargenOperativoMinutos, IReadOnlyList<ParadaEstimable> Paradas);

public enum EstadoEstimacionInicial
{
    Calculada, DatosInsuficientes, SinParadas, ProveedorNoConfigurado, RutaNoEncontrada,
    ProveedorNoDisponible, TiempoAgotado, CuotaExcedida, RespuestaInvalida, PosicionNoAdmisible, EjecucionNoDisponible
}
public enum EstadoTramoVial { Encontrado, RutaNoEncontrada, ProveedorNoDisponible, TiempoAgotado, CuotaExcedida, RespuestaInvalida, NoConfigurado }
public enum FuenteVial { ProveedorVial, RespuestaControlada }
public enum ModeloTrafico { SinTrafico, Aproximado, Predictivo, Actual }
public enum VigenciaEstimacion { Desconocida, Vigente, Vencida, Invalidada }

// Only coordinates/profile/timing and an opaque correlation travel to the adapter: no customer names, addresses or tenant IDs.
public sealed record SolicitudTramoVial(string Huella, int Indice, PuntoVial Origen, PuntoVial Destino,
    DateTimeOffset SalidaEnUtc, string PerfilVial);
public sealed record ProcedenciaVial(string Proveedor, string Modelo, FuenteVial Fuente, ModeloTrafico Trafico,
    string Atribucion, DateTimeOffset CalculadoEnUtc, DateTimeOffset? ValidoHastaUtc);
public sealed record RespuestaTramoVial(SolicitudTramoVial Solicitud, EstadoTramoVial Estado,
    double? DuracionSegundos = null, double? DistanciaMetros = null, ProcedenciaVial? Procedencia = null);

public interface IProveedorTramosViales
{
    Task<RespuestaTramoVial> CalcularAsync(SolicitudTramoVial solicitud, CancellationToken ct);
}

public sealed record HorarioCalculado(Guid EnvioId, DateTimeOffset LlegadaManualEnUtc,
    DateTimeOffset LlegadaCalculadaEnUtc, DateTimeOffset SalidaCalculadaEnUtc);
public sealed record TramoCalculado(SolicitudTramoVial Solicitud, double DuracionSegundos,
    double DistanciaMetros, ProcedenciaVial Procedencia);
// This is a separate, read-only theoretical itinerary, not a replacement for manual commitments or physical facts.
public sealed record EstimacionInicial(EstadoEstimacionInicial Estado, string Huella, DateTimeOffset IntentadoEnUtc,
    DateTimeOffset? CalculadoEnUtc, string ZonaHoraria, IReadOnlyList<HorarioCalculado> Paradas,
    IReadOnlyList<TramoCalculado> Tramos, DateTimeOffset? RegresoEstimadoBaseEnUtc,
    DateTimeOffset? ListoPrevistoEnUtc, DateTimeOffset? ValidoHastaUtc);
