using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace NexoRuta.Api.Features.Reparto.Contracts;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class OperacionRepartoRequest
{
    public Guid Id { get; init; }
    [Range(1, long.MaxValue)] public long Secuencia { get; init; }
    [Required, StringLength(40)] public string Tipo { get; init; } = "";
    public Guid? EnvioId { get; init; }
    [Range(0, long.MaxValue)] public long? VersionEnvio { get; init; }
    public DateTimeOffset OcurridaEn { get; init; }
    public JsonElement Datos { get; init; }
}
