using System.ComponentModel.DataAnnotations;

namespace NexoRuta.Api.Contracts.Auth;

public sealed class RestablecerPasswordRequest
{
    [Required]
    public Guid UsuarioId { get; init; }

    [Required, StringLength(4096, MinimumLength = 1)]
    public string Token { get; init; } = "";

    [Required, StringLength(1024, MinimumLength = 1)]
    public string NuevaPassword { get; init; } = "";
}
