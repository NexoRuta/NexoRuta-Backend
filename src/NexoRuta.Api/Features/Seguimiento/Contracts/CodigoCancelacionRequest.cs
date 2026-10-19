using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
namespace NexoRuta.Api.Features.Seguimiento.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CodigoCancelacionRequest
{
    [Required, StringLength(32)] public string Codigo { get; init; } = "";
}
