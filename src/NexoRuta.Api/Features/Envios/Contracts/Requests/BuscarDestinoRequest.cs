using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace NexoRuta.Api.Features.Envios.Contracts.Requests;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class BuscarDestinoRequest
{
    [Required, StringLength(240, MinimumLength = 3)]
    public string Consulta { get; set; } = "";
}
