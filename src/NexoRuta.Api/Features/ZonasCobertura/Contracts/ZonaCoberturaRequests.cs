using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexoRuta.Api.Features.ZonasCobertura.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class GeoJsonGeometryRequest
{
    [Required, RegularExpression("^(Polygon|MultiPolygon)$")] public string Type { get; init; } = "";
    [Required] public JsonElement Coordinates { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CrearZonaCoberturaRequest
{
    [Required, StringLength(120)] public string Nombre { get; init; } = "";
    public GeoJsonGeometryRequest? Geometria { get; init; }
    [StringLength(16)] public string? CodigoDepartamento { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ActualizarZonaCoberturaRequest
{
    [Required, StringLength(120)] public string Nombre { get; init; } = "";
    [Required] public GeoJsonGeometryRequest? Geometria { get; init; }
    [Required, Range(0, long.MaxValue)] public long? Version { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CambiarEstadoZonaRequest
{
    [Required, RegularExpression("^(Activa|Inactiva)$")] public string Estado { get; init; } = "";
    [Required, Range(0, long.MaxValue)] public long? Version { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CrearFranjaEntregaRequest
{
    [Required, Range(1, 7)] public short? DiaSemanaIso { get; init; }
    [Required] public TimeOnly? HoraInicio { get; init; }
    [Required] public TimeOnly? HoraFin { get; init; }
    [Required, Range(0, long.MaxValue)] public long? VersionZona { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ActualizarFranjaEntregaRequest
{
    [Required, Range(1, 7)] public short? DiaSemanaIso { get; init; }
    [Required] public TimeOnly? HoraInicio { get; init; }
    [Required] public TimeOnly? HoraFin { get; init; }
    [Required, RegularExpression("^(Activa|Inactiva)$")] public string Estado { get; init; } = "";
    [Required, Range(0, long.MaxValue)] public long? VersionZona { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ConsultarCoberturaRequest
{
    [Required, Range(-180, 180)] public double? Longitud { get; init; }
    [Required, Range(-90, 90)] public double? Latitud { get; init; }
}
