using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace NexoRuta.Api.Contracts.Usuarios;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CambiarEmailUsuarioRequest
{
    [Required, EmailAddress, MaxLength(254)]
    public string Email { get; init; } = "";

    [Required, Range(0, long.MaxValue)]
    public long? Version { get; init; }
}
