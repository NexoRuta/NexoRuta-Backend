using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace NexoRuta.Api.Features.Envios.Contracts.Requests;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CancelarEnvioRequest
{
    [JsonRequired, Range(0, long.MaxValue)] public long Version { get; init; }
    [Required, StringLength(500)] public string Motivo { get; init; } = "";
}
