using System.ComponentModel.DataAnnotations;
namespace NexoRuta.Api.Features.Reparto.Contracts;

public sealed class ReasignarEnvioRequest
{
    public Guid RutaDestinoId { get; init; }
    [Range(0, long.MaxValue)] public long VersionEnvio { get; init; }
    [Range(0, long.MaxValue)] public long VersionRutaDestino { get; init; }
    public DateTimeOffset LlegadaEstimadaUtc { get; init; }
    [Required, StringLength(500)] public string Motivo { get; init; } = "";
}
