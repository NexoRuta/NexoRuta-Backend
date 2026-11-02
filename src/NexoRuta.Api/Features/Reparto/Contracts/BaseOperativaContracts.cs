using System.ComponentModel.DataAnnotations;
using NexoRuta.Application.Reparto.Dtos;
namespace NexoRuta.Api.Features.Reparto.Contracts;
public sealed record GuardarBaseOperativaRequest([Required, Range(0, long.MaxValue)] long? Version, [Required, StringLength(500)] string Direccion,
    [Range(0, int.MaxValue)] int? ServicioPredeterminadoMinutos, [Range(0, int.MaxValue)] int? MargenRetornoMinutos);
public sealed record ConfirmarBaseOperativaRequest([Required, Range(0, long.MaxValue)] long? Version,
    [Required, Range(-90d, 90d)] double? Latitud, [Required, Range(-180d, 180d)] double? Longitud);
public sealed record BaseOperativaResponse(long Version, string Direccion, double? Latitud, double? Longitud,
    DateTimeOffset? ConfirmadaEnUtc, Guid? ConfirmadaPorAccesoId, int? ServicioPredeterminadoMinutos, int? MargenRetornoMinutos)
{
    public static BaseOperativaResponse Desde(BaseOperativaDatos b) => new(b.Version, b.Direccion, b.Latitud, b.Longitud, b.ConfirmadaEnUtc, b.ConfirmadaPorAccesoId, b.ServicioPredeterminadoMinutos, b.MargenRetornoMinutos);
}
public sealed record PropuestaBaseResponse(string Descripcion, double Latitud, double Longitud);
public sealed record PropuestasBaseResponse(long Version, string Direccion, string Atribucion, IReadOnlyList<PropuestaBaseResponse> Propuestas);
public sealed record PlanTemporalResponse(DateTimeOffset? SalidaPlanificadaEnUtc, string ZonaHoraria, long? BaseVersion,
    string? DireccionBase, double? LatitudBase, double? LongitudBase, DateTimeOffset? BaseConfirmadaEnUtc,
    Guid? BaseConfirmadaPorAccesoId, int? ServicioPredeterminadoMinutos, int? MargenRetornoMinutos, long VersionRutaAlPlanificar, IReadOnlyList<Guid> OrdenEnvios)
{
    public static PlanTemporalResponse Desde(PlanTemporalDatos t) => new(t.SalidaPlanificadaEnUtc,t.ZonaHoraria,t.BaseVersion,t.DireccionBase,t.LatitudBase,t.LongitudBase,t.BaseConfirmadaEnUtc,t.BaseConfirmadaPorAccesoId,t.ServicioPredeterminadoMinutos,t.MargenRetornoMinutos,t.VersionRutaAlPlanificar,t.OrdenEnvios);
}
