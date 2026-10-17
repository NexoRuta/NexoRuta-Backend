using System.ComponentModel.DataAnnotations;
namespace NexoRuta.Api.Features.Reparto.Contracts;
public sealed class PlanificarRutaRequest
{
    public Guid RepartidorAccesoId { get; init; }
    public Guid VehiculoId { get; init; }
    public DateOnly Fecha { get; init; }
    [Required, MinLength(1), MaxLength(500)] public ParadaPlanificadaRequest[] Paradas { get; init; } = [];
}
