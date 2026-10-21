using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
namespace NexoRuta.Api.Features.Envios.Contracts.Requests;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConfirmarDestinoRequest([Required, Range(0, long.MaxValue)] long? Version,
    [Required, Range(-90, 90)] double? Latitud, [Required, Range(-180, 180)] double? Longitud);
