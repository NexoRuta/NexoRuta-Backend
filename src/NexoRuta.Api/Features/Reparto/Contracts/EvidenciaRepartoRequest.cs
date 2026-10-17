using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
namespace NexoRuta.Api.Features.Reparto.Contracts;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class EvidenciaRepartoRequest
{
    public Guid EnvioId { get; init; }
    [Required, StringLength(10)] public string Tipo { get; init; } = "";
    [Required, MinLength(16), MaxLength(2097152)] public byte[] Contenido { get; init; } = [];
}
