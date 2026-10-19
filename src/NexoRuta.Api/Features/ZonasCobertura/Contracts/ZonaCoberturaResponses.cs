using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexoRuta.Domain.ZonasCobertura;
using NetTopologySuite.Geometries;

namespace NexoRuta.Api.Features.ZonasCobertura.Contracts;

public sealed record ZonaCoberturaResumenResponse(
    Guid Id, string Nombre, string Estado, int FranjasActivas, long Version, int RevisionActual, DateTimeOffset ActualizadaUtc);

public sealed record PaginaZonasCoberturaResponse(
    IReadOnlyList<ZonaCoberturaResumenResponse> Items, int Pagina, int TamanoPagina, int Total);

public sealed record FranjaEntregaResponse(
    Guid Id, short DiaSemanaIso,
    [property: JsonConverter(typeof(TimeOnlyMicrosecondsJsonConverter))] TimeOnly HoraInicio,
    [property: JsonConverter(typeof(TimeOnlyMicrosecondsJsonConverter))] TimeOnly HoraFin,
    string Estado);

public sealed class TimeOnlyMicrosecondsJsonConverter : JsonConverter<TimeOnly>
{
    private static readonly JsonConverter<TimeOnly> DefaultConverter =
        (JsonConverter<TimeOnly>)JsonSerializerOptions.Default.GetConverter(typeof(TimeOnly));

    public override TimeOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => DefaultConverter.Read(ref reader, typeToConvert, options);

    public override void Write(Utf8JsonWriter writer, TimeOnly value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString("HH:mm:ss.ffffff", CultureInfo.InvariantCulture));
}

public sealed record ZonaCoberturaDetalleResponse(
    Guid Id, string Nombre, string Estado, GeoJsonGeometryResponse Geometria, string ZonaHoraria,
    IReadOnlyList<FranjaEntregaResponse> Franjas, long Version, int RevisionActual,
    string OrigenGeometria, string? CodigoDepartamento, string? VersionFuente,
    DateTimeOffset CreadaUtc, DateTimeOffset ActualizadaUtc);

public sealed record FranjasEntregaResponse(
    IReadOnlyList<FranjaEntregaResponse> Items, long VersionZona, int RevisionActual);

public sealed record FranjaEntregaCambioResponse(FranjaEntregaResponse Franja, long VersionZona, int RevisionActual);

public sealed record GeoJsonGeometryResponse(string Type, JsonElement Coordinates);

public sealed record DepartamentoReferenciaResponse(
    string Codigo, string VersionFuente, string Nombre, string UrlFuente,
    string Atribucion, string Licencia, DateTimeOffset ImportadaUtc);

public sealed record BoundsGeoJsonResponse(double Oeste, double Sur, double Este, double Norte);

public sealed record DepartamentoReferenciaGeometriaResponse(
    string Codigo,
    string VersionFuente,
    string Nombre,
    GeoJsonGeometryResponse Geometria,
    BoundsGeoJsonResponse Limites);

public sealed record TerritorioUruguayResponse(
    string CodigoPais,
    string VersionFuente,
    GeoJsonGeometryResponse Geometria,
    BoundsGeoJsonResponse Limites);

public sealed record CoincidenciaCoberturaResponse(
    Guid ZonaId, string Nombre, int RevisionActual, IReadOnlyList<FranjaEntregaResponse> FranjasActivas);

public sealed record ConsultaCoberturaResponse(
    string Resultado, bool RequiereSeleccionDeZona, IReadOnlyList<CoincidenciaCoberturaResponse> Coincidencias);

public static class ZonaCoberturaResponseMapper
{
    public static GeoJsonGeometryResponse GeometriaDesde(Geometry geometry)
    {
        using var document = JsonDocument.Parse(new NetTopologySuite.IO.GeoJsonWriter().Write(geometry));
        return new(document.RootElement.GetProperty("type").GetString()!,
            document.RootElement.GetProperty("coordinates").Clone());
    }

    public static FranjaEntregaResponse Desde(FranjaEntrega slot)
        => new(slot.Id, (short)slot.DiaSemanaIso, slot.HoraInicio, slot.HoraFin, slot.Estado.ToString());

    public static ZonaCoberturaDetalleResponse Desde(ZonaCobertura zone)
    {
        var geometry = GeometriaDesde(zone.Geometria);
        return new(zone.Id, zone.Nombre, zone.Estado.ToString(), geometry, zone.ZonaHoraria,
            zone.Franjas.OrderBy(x => x.DiaSemanaIso).ThenBy(x => x.HoraInicio).Select(Desde).ToArray(),
            zone.Version, zone.RevisionActual, zone.OrigenGeometria.ToString(), zone.CodigoDepartamento,
            zone.VersionFuente, zone.CreadaUtc, zone.ActualizadaUtc);
    }
}
