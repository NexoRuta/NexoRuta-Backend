using NexoRuta.Application.Envios.Dtos;
namespace NexoRuta.Application.Reparto.Dtos;
public sealed record BaseOperativaDatos(long Version, string Direccion, double? Latitud, double? Longitud,
    DateTimeOffset? ConfirmadaEnUtc, Guid? ConfirmadaPorAccesoId, int? ServicioPredeterminadoMinutos, int? MargenRetornoMinutos);
public sealed record GuardarBaseOperativaDatos(long Version, string Direccion, int? ServicioPredeterminadoMinutos, int? MargenRetornoMinutos);
public sealed record PropuestasBaseDatos(long Version, string Direccion, string Atribucion, IReadOnlyList<PropuestaDestinoDatos> Propuestas);
public sealed record PlanTemporalDatos(DateTimeOffset? SalidaPlanificadaEnUtc, string ZonaHoraria, long? BaseVersion,
    string? DireccionBase, double? LatitudBase, double? LongitudBase, DateTimeOffset? BaseConfirmadaEnUtc,
    Guid? BaseConfirmadaPorAccesoId, int? ServicioPredeterminadoMinutos, int? MargenRetornoMinutos,
    long VersionRutaAlPlanificar, IReadOnlyList<Guid> OrdenEnvios);
