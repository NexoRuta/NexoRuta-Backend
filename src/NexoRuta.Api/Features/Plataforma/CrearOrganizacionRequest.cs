using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace NexoRuta.Api.Features.Plataforma;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CrearOrganizacionRequest
{
    [Required, MaxLength(160)]
    public string Nombre { get; init; } = "";
    [Required]
    public PrimerAdministradorRequest Administrador { get; init; } = null!;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PrimerAdministradorRequest
{
    [Required, EmailAddress, MaxLength(254)]
    public string Email { get; init; } = "";
    [Required, MinLength(12)]
    public string Password { get; init; } = "";
}
