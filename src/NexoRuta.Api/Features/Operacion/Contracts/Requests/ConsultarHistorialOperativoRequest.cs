using System.ComponentModel.DataAnnotations;

namespace NexoRuta.Api.Features.Operacion.Contracts.Requests;

public sealed class ConsultarHistorialOperativoRequest
{
    [Required]
    public Guid? OperadorId { get; init; }

    [Required]
    public DateTimeOffset? Desde { get; init; }

    [Required]
    public DateTimeOffset? Hasta { get; init; }

    public Guid? EnvioId { get; init; }

    [StringLength(32)]
    public string? Tipo { get; init; }

    [Range(0, 10000)]
    public int Offset { get; init; }

    [Range(1, 100)]
    public int Limite { get; init; } = 50;
}
