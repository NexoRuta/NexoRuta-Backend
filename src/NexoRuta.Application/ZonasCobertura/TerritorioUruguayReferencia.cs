using NetTopologySuite.Geometries;

namespace NexoRuta.Application.ZonasCobertura;

public enum ResultadoValidacionTerritorial
{
    CatalogoNoDisponible,
    Cubierta,
    FueraDelTerritorio
}

public sealed record DepartamentoReferenciaConGeometria(
    string Codigo,
    string VersionFuente,
    string Nombre,
    Geometry Geometria,
    Envelope Limites);

public sealed record TerritorioUruguayReferencia(
    string CodigoPais,
    string VersionFuente,
    Geometry Geometria,
    Geometry GeometriaConDepartamentos,
    Envelope Limites);
