
using System.ComponentModel.DataAnnotations;

namespace NexoRuta.Api.Contracts.Usuarios;

public sealed class CambiarEstadoUsuarioRequest
{
    [Required]
    public bool? Activo { get; init; }
}
