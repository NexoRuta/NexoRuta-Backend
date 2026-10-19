using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using NexoRuta.Domain.ZonasCobertura;
using NexoRuta.Infrastructure.Persistence;
using NetTopologySuite.Operation.Valid;
using NetTopologySuite.Operation.Union;

namespace NexoRuta.Infrastructure.ZonasCobertura;

public sealed record DepartamentoCatalogImportResult(int DepartmentCount, string VersionFuente, bool AlreadyImported);

public sealed class DepartamentoCatalogImporter(NexoRutaDbContext db)
{
    public const string ImportCommandName = "department-catalog-import";
    public const string AllowUpdateOption = "--allow-source-update";

    public async Task<DepartamentoCatalogImportResult> ImportarCatalogoIncluidoAsync(
        bool permitirActualizacionExplicita = false,
        CancellationToken cancellationToken = default)
    {
        await using var stream = typeof(DepartamentoCatalogImporter).Assembly
            .GetManifestResourceStream(CatalogoDepartamentosUruguay.ResourceName)
            ?? throw new InvalidOperationException("El GeoJSON departamental no está incluido en el ensamblado publicado.");
        return await ImportarAsync(stream, permitirActualizacionExplicita, cancellationToken);
    }

    public async Task<DepartamentoCatalogImportResult> ImportarAsync(
        Stream geoJson,
        bool permitirActualizacionExplicita = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(geoJson);
        using var content = new MemoryStream();
        await geoJson.CopyToAsync(content, cancellationToken);
        var bytes = content.ToArray();
        var version = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        var departments = ParseAndValidate(bytes);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var sameVersion = await db.DepartamentosReferencia.AsNoTracking()
            .Where(x => x.VersionFuente == version)
            .Select(x => x.Codigo)
            .ToArrayAsync(cancellationToken);

        if (sameVersion.Length > 0)
        {
            if (sameVersion.Length != departments.Count
                || !sameVersion.ToHashSet(StringComparer.Ordinal).SetEquals(departments.Select(x => x.Code)))
                throw new InvalidOperationException("La versión del catálogo ya existe incompleta; no se alteraron sus datos.");

            await transaction.RollbackAsync(cancellationToken);
            return new(departments.Count, version, AlreadyImported: true);
        }

        var existing = await db.DepartamentosReferencia.AsNoTracking()
            .Select(x => new { x.Codigo, x.ImportadaUtc })
            .ToArrayAsync(cancellationToken);
        if (existing.Length > 0 && !permitirActualizacionExplicita)
            throw new InvalidOperationException("Ya existe otra versión del catálogo. Confirmá una actualización explícita para agregar la nueva referencia.");

        var importedAt = DateTimeOffset.UtcNow;
        if (existing.Length > 0 && importedAt <= existing.Max(x => x.ImportadaUtc))
            importedAt = existing.Max(x => x.ImportadaUtc).AddTicks(10);

        db.DepartamentosReferencia.AddRange(departments.Select(x => new DepartamentoReferencia(
            x.Code,
            version,
            x.Name,
            x.Geometry,
            CatalogoDepartamentosUruguay.SourceUrl,
            CatalogoDepartamentosUruguay.Attribution,
            CatalogoDepartamentosUruguay.UndeclaredLicense,
            importedAt)));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(departments.Count, version, AlreadyImported: false);
    }

    private static IReadOnlyList<DepartamentoImportado> ParseAndValidate(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 128 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || type.GetString() != "FeatureCollection"
                || !root.TryGetProperty("features", out var features)
                || features.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("El archivo no contiene un GeoJSON FeatureCollection válido.");

            var factory = new GeometryFactory(new PrecisionModel(), DepartamentoReferencia.GeometriaSrid);
            var polygonsByName = new Dictionary<string, List<Polygon>>(StringComparer.Ordinal);
            foreach (var feature in features.EnumerateArray())
            {
                if (!feature.TryGetProperty("type", out var featureType) || featureType.GetString() != "Feature"
                    || !feature.TryGetProperty("properties", out var properties)
                    || !properties.TryGetProperty("DEPTO", out var departmentName)
                    || departmentName.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Cada feature debe tener el campo DEPTO de tipo texto.");

                var name = departmentName.GetString()?.Trim() ?? "";
                if (!CatalogoDepartamentosUruguay.DepartmentCodes.ContainsKey(name))
                    throw new InvalidDataException("El catálogo contiene un departamento sin correspondencia explícita de código.");
                if (!feature.TryGetProperty("geometry", out var geometryJson) || geometryJson.ValueKind != JsonValueKind.Object
                    || !geometryJson.TryGetProperty("coordinates", out var coordinates)
                    || !geometryJson.TryGetProperty("type", out var geometryType))
                    throw new InvalidDataException("Una feature departamental no contiene geometría.");

                var polygons = geometryType.GetString() switch
                {
                    "Polygon" => [ParsePolygon(factory, coordinates)],
                    "MultiPolygon" => ParseMultiPolygon(factory, coordinates),
                    _ => throw new InvalidDataException("Las geometrías departamentales deben ser Polygon o MultiPolygon.")
                };

                foreach (var polygon in polygons)
                {
                    if (polygon.IsEmpty || !polygon.IsValid)
                        throw new InvalidDataException("Una geometría departamental está vacía o no es válida.");
                    if (!polygonsByName.TryGetValue(name, out var list)) polygonsByName.Add(name, list = []);
                    if (list.Any(existingPolygon => existingPolygon.EqualsExact(polygon)))
                        throw new InvalidDataException("El archivo repite una misma parte geométrica departamental.");
                    list.Add(polygon);
                }
            }

            if (polygonsByName.Count != CatalogoDepartamentosUruguay.DepartmentCodes.Count
                || !polygonsByName.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(CatalogoDepartamentosUruguay.DepartmentCodes.Keys))
                throw new InvalidDataException("El catálogo debe incluir exactamente los 19 departamentos uruguayos.");

            var result = new List<DepartamentoImportado>(polygonsByName.Count);
            foreach (var (name, polygons) in polygonsByName.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                var geometry = factory.CreateMultiPolygon(polygons.ToArray());
                if (!geometry.IsValid)
                {
                    // Some source features for one department overlap. Dissolve their exact set union;
                    // this removes invalid internal overlap boundaries without simplifying or clipping.
                    var dissolved = UnaryUnionOp.Union(polygons.Cast<Geometry>());
                    geometry = dissolved switch
                    {
                        Polygon polygon => factory.CreateMultiPolygon([polygon]),
                        MultiPolygon multiPolygon => factory.CreateMultiPolygon(multiPolygon.Geometries.Cast<Polygon>().ToArray()),
                        _ => geometry
                    };
                }
                if (geometry.IsEmpty || !geometry.IsValid || geometry.SRID != DepartamentoReferencia.GeometriaSrid)
                {
                    var validationError = new IsValidOp(geometry).ValidationError;
                    var reason = validationError is null ? "SRID o geometría vacía"
                        : $"{validationError.Message} en {validationError.Coordinate}";
                    throw new InvalidDataException($"La geometría multipoligonal del departamento '{name}' no es válida: {reason}.");
                }
                if (geometry.EnvelopeInternal.MinX < -60 || geometry.EnvelopeInternal.MaxX > -50
                    || geometry.EnvelopeInternal.MinY < -40 || geometry.EnvelopeInternal.MaxY > -25)
                    throw new InvalidDataException("Las coordenadas no son compatibles con la extensión de Uruguay en longitud/latitud.");
                result.Add(new(CatalogoDepartamentosUruguay.DepartmentCodes[name], name, geometry));
            }

            return result;
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("El archivo de catálogo no es un JSON válido.", error);
        }
        catch (ArgumentException error)
        {
            throw new InvalidDataException("El archivo contiene una geometría departamental mal formada.", error);
        }
    }

    private static Polygon ParsePolygon(GeometryFactory factory, JsonElement coordinates)
    {
        if (coordinates.ValueKind != JsonValueKind.Array || coordinates.GetArrayLength() == 0)
            throw new InvalidDataException("Un Polygon debe contener al menos un anillo.");
        var rings = coordinates.EnumerateArray().Select(ring =>
        {
            if (ring.ValueKind != JsonValueKind.Array || ring.GetArrayLength() < 4)
                throw new InvalidDataException("Cada anillo debe contener al menos cuatro posiciones.");
            return factory.CreateLinearRing(ring.EnumerateArray().Select(ParsePosition).ToArray());
        }).ToArray();
        return factory.CreatePolygon(rings[0], rings.Skip(1).ToArray());
    }

    private static IReadOnlyList<Polygon> ParseMultiPolygon(GeometryFactory factory, JsonElement coordinates)
    {
        if (coordinates.ValueKind != JsonValueKind.Array || coordinates.GetArrayLength() == 0)
            throw new InvalidDataException("Un MultiPolygon debe contener al menos un polígono.");
        return coordinates.EnumerateArray().Select(polygon => ParsePolygon(factory, polygon)).ToArray();
    }

    private static Coordinate ParsePosition(JsonElement position)
    {
        if (position.ValueKind != JsonValueKind.Array || position.GetArrayLength() != 2)
            throw new InvalidDataException("Las coordenadas deben ser pares bidimensionales longitud/latitud.");
        var values = position.EnumerateArray().ToArray();
        if (values.Any(value => value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number)))
            throw new InvalidDataException("Las coordenadas deben ser números finitos.");
        var longitude = values[0].GetDouble();
        var latitude = values[1].GetDouble();
        if (longitude is < -180 or > 180 || latitude is < -90 or > 90)
            throw new InvalidDataException("Las coordenadas no están en el rango longitud/latitud WGS 84.");
        return new Coordinate(longitude, latitude);
    }

    private sealed record DepartamentoImportado(string Code, string Name, MultiPolygon Geometry);
}
