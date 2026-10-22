using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace NexoRuta.Api.Features.Organizaciones.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ActualizarNombreOrganizacionRequest
{
    [Required, StringLength(160)]
    public string Nombre { get; init; } = "";

    [Required, Range(0, long.MaxValue)]
    public long? Version { get; init; }
}
