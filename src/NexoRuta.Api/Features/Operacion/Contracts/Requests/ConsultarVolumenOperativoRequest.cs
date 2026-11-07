using System.ComponentModel.DataAnnotations;

namespace NexoRuta.Api.Features.Operacion.Contracts.Requests;

public sealed class ConsultarVolumenOperativoRequest
{
    [Required]
    public Guid? OperadorId { get; init; }

    [Required]
    public DateTimeOffset? Desde { get; init; }

    [Required]
    public DateTimeOffset? Hasta { get; init; }
}
