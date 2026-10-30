using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace NexoRuta.Api.Features.Reparto.Contracts;

public sealed class IniciarDevolucionEnvioRequest
{
    [JsonRequired, Range(0, long.MaxValue)] public long VersionEnvio { get; init; }
    [Required] public Guid ComandoId { get; init; }
    [Required, StringLength(500, MinimumLength = 1)] public string Motivo { get; init; } = "";
}
