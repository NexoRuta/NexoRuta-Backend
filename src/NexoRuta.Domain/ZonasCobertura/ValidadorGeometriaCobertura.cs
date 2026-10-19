using NetTopologySuite.Geometries;

namespace NexoRuta.Domain.ZonasCobertura;

internal static class ValidadorGeometriaCobertura
{
    public const int Srid = DepartamentoReferencia.GeometriaSrid;

    public static MultiPolygon Normalizar(Geometry geometry, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(geometry, parameterName);
        if (geometry.SRID != Srid)
            throw new ArgumentException("La geometría debe usar SRID 4326.", parameterName);
        if (geometry.IsEmpty)
            throw new ArgumentException("La geometría no puede estar vacía.", parameterName);

        var normalized = geometry switch
        {
            Polygon polygon => polygon.Factory.CreateMultiPolygon([ (Polygon)polygon.Copy() ]),
            MultiPolygon multiPolygon => (MultiPolygon)multiPolygon.Copy(),
            _ => throw new ArgumentException("La geometría debe ser Polygon o MultiPolygon.", parameterName)
        };

        foreach (var coordinate in normalized.Coordinates)
        {
            if (!double.IsFinite(coordinate.X) || !double.IsFinite(coordinate.Y)
                || coordinate.X is < -180 or > 180 || coordinate.Y is < -90 or > 90)
                throw new ArgumentException("La geometría contiene coordenadas fuera del rango WGS 84.", parameterName);
            if (!double.IsNaN(coordinate.Z))
                throw new ArgumentException("La geometría debe ser bidimensional.", parameterName);
        }

        if (!normalized.IsValid)
            throw new ArgumentException("La geometría no es válida; corregí los anillos y sus intersecciones.", parameterName);

        normalized.SRID = Srid;
        return normalized;
    }
}
