using System.ComponentModel.DataAnnotations;

namespace NexoRuta.Api.Features.Autenticacion.Contracts;

public sealed class RenovacionSesionRequest
{
    [Required, StringLength(43, MinimumLength = 43), RegularExpression("^[A-Za-z0-9_-]{43}$")]
    public string RefreshToken { get; init; } = "";
}
