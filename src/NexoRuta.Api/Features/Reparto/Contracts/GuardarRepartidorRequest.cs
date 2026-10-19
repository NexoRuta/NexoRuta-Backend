using System.ComponentModel.DataAnnotations;

namespace NexoRuta.Api.Features.Reparto.Contracts;

public sealed class GuardarRepartidorRequest
{
    [Required, StringLength(120)]
    public string Nombre { get; init; } = "";
    public bool Activo { get; init; } = true;
    [Range(0, long.MaxValue)]
    public long? Version { get; init; }
}
