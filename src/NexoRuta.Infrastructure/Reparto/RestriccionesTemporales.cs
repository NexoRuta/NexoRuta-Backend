using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Reparto.Exceptions;
using NexoRuta.Infrastructure.Persistence;
namespace NexoRuta.Infrastructure.Reparto;

internal static class RestriccionesTemporales
{
    // Coverage revision captured at admission is the commitment; never use today's zone.
    internal static async Task ValidarAsync(NexoRutaDbContext db, Guid operador, Guid envio,
        DateTimeOffset llegada, int? servicio, CancellationToken ct)
    {
        var retiro = await db.Envios.AsNoTracking().SingleAsync(e => e.OperadorId == operador && e.Id == envio, ct);
        if (retiro.EsRetiro && (llegada < retiro.FranjaRetiroDesdeUtc || llegada.AddMinutes(servicio ?? 0) > retiro.FranjaRetiroHastaUtc))
            throw new ConflictoRepartoException("La visita no cabe en la franja solicitada para el retiro.");
        if (!retiro.EsRetiro && retiro.Estado == NexoRuta.Domain.Envios.EstadoEnvio.EntregadoParcialmente)
        {
            var pendientes = await db.Bultos.AsNoTracking().Where(b => b.OperadorId == operador && b.EnvioId == envio && b.Resultado == NexoRuta.Domain.Envios.ResultadoBulto.Pendiente).ToArrayAsync(ct);
            var reglas = await db.VersionesReglasOperativas.AsNoTracking().SingleAsync(v => v.OperadorId == operador && v.Id == retiro.VersionReglasId, ct);
            var intentos = await IntentosPorBulto.Calcular(db, operador, envio, pendientes, reglas.IntervaloEntreIntentosMinutos!.Value, ct);
            foreach (var intento in intentos.Values) intento.ExigirSiguienteIntento(llegada, reglas.MaxIntentosEntrega!.Value);
        }
        var snapshot = await db.Database.SqlQuery<string>($"""
            SELECT r."SnapshotJson"::text AS "Value" FROM (
                SELECT * FROM public."TarifasEnvio" WHERE "OperadorId"={operador} AND "EnvioId"={envio}
                ORDER BY "CreadoEnUtc" DESC,"Id" DESC LIMIT 1
            ) t
            JOIN public."ZonasCoberturaRevisiones" r ON r."OperadorId"=t."OperadorId"
                AND r."ZonaId"=(t."Cobertura"->>'ZonaId')::uuid
                AND r."NumeroRevision"=(t."Cobertura"->>'RevisionZona')::int
            """).SingleOrDefaultAsync(ct);
        if (snapshot is null) throw new ConflictoRepartoException($"El envío {envio} no tiene una revisión de cobertura verificable.");
        using var document = JsonDocument.Parse(snapshot);
        var root = document.RootElement;
        var zona = TimeZoneInfo.FindSystemTimeZoneById(root.GetProperty("zonaHoraria").GetString()!);
        foreach (var slot in root.GetProperty("franjas").EnumerateArray())
        {
            if (slot.GetProperty("estado").GetString() != "Activa") continue;
            var ventana = new NexoRuta.Domain.Reparto.VentanaRuta(slot.GetProperty("diaSemanaIso").GetInt32(),
                TimeOnly.Parse(slot.GetProperty("horaInicio").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                TimeOnly.Parse(slot.GetProperty("horaFin").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
            if (ventana.Contiene(llegada, servicio ?? 0, zona)) return;
        }
        throw new ConflictoRepartoException($"La llegada y el servicio del envío {envio} no caben en las franjas de su cobertura comprometida.");
    }
}
