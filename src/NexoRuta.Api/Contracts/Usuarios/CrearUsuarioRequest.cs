
using System.ComponentModel.DataAnnotations;

namespace NexoRuta.Api.Contracts.Usuarios;

public sealed class CrearUsuarioRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(254)]
    public string Email { get; init; } = string.Empty;

    [Required]
    [MinLength(12)]
    public string Password { get; init; } = string.Empty;

    [Required]
    public Guid RolId { get; init; }
}
