using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace NexoRuta.Api.Features.Usuarios.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AltaPropiaRequest
{
    [Required, StringLength(160)]
    public string Nombre { get; init; } = "";
}
