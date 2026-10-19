using NetTopologySuite.IO;
using NexoRuta.Domain.ZonasCobertura;
using System.Text.Json;

namespace NexoRuta.Infrastructure.ZonasCobertura;

internal static class ZonaCoberturaSnapshotJson
{
    public static string Crear(ZonaCobertura zona)
    {
        using var geometry = JsonDocument.Parse(new GeoJsonWriter().Write(zona.Geometria));
        var snapshot = new
        {
            schemaVersion = 1,
            id = zona.Id,
            operadorId = zona.OperadorId,
            nombre = zona.Nombre,
            estado = zona.Estado.ToString(),
            zonaHoraria = zona.ZonaHoraria,
            srid = 4326,
            geometria = geometry.RootElement,
            version = zona.Version,
            numeroRevision = zona.RevisionActual,
            origenGeometria = zona.OrigenGeometria.ToString(),
            codigoDepartamento = zona.CodigoDepartamento,
            versionFuente = zona.VersionFuente,
            creadaUtc = zona.CreadaUtc,
            actualizadaUtc = zona.ActualizadaUtc,
            franjas = zona.Franjas
                .OrderBy(x => x.DiaSemanaIso)
                .ThenBy(x => x.HoraInicio)
                .ThenBy(x => x.Id)
                .Select(x => new
                {
                    x.Id,
                    x.OperadorId,
                    x.ZonaId,
                    diaSemanaIso = (int)x.DiaSemanaIso,
                    horaInicio = x.HoraInicio.ToString("HH:mm:ss.ffffff", System.Globalization.CultureInfo.InvariantCulture),
                    horaFin = x.HoraFin.ToString("HH:mm:ss.ffffff", System.Globalization.CultureInfo.InvariantCulture),
                    estado = x.Estado.ToString()
                })
        };

        return JsonSerializer.Serialize(snapshot);
    }
}
