using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Operacion;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Operacion;

public sealed record OperationalSnapshot(long Revision, DateTimeOffset ConsultadaEnUtc, string EstadoProyeccion,
    IReadOnlyList<EventoOperativo> Entidades)
{
    public IReadOnlyList<RiesgoEnvio> Riesgos { get; init; } = [];
}
public sealed record RiesgoEnvio(Guid EnvioId, string Riesgo);

public sealed class OperationalReadStore(NexoRutaDbContext db, TimeProvider clock)
{
    public sealed record HealthSnapshot(long Capturados, long Documentados, string Estado, DateTimeOffset VerificadaEnUtc);
    public Task<HealthSnapshot> ReadHealthAsync(string projection, Guid operatorId, CancellationToken ct)
        => db.Database.SqlQuery<HealthSnapshot>($"""
            SELECT coalesce(f."Capturados",0)::bigint AS "Capturados",coalesce(f."Documentados",0)::bigint AS "Documentados",
              nexoruta_app.operational_projection_status({projection},{operatorId}) AS "Estado",statement_timestamp() AS "VerificadaEnUtc"
            FROM (SELECT {operatorId}::uuid AS id)scope LEFT JOIN public."OperacionFuente" f ON f."OperadorId"=scope.id
            """).SingleAsync(ct);

    public async Task<OperationalSnapshot> SnapshotAsync(Guid operatorId, CancellationToken ct)
    {
        if (operatorId == Guid.Empty) throw new ArgumentException("Operator scope is required.");
        // A single SQL statement observes all rows and their revisions in the same MVCC snapshot.
        var photograph = await db.Database.SqlQuery<Photograph>($"""
            SELECT coalesce(jsonb_agg(jsonb_build_object('revision',r."Revision",'evento',r."Evento") ORDER BY r."Tipo",r."EntidadId"),'[]'::jsonb)::text AS "Filas",
            nexoruta_app.operational_projection_status('board',{operatorId}) AS "Estado"
            FROM (SELECT "Revision","Evento","Tipo","EntidadId" FROM public."OperacionProyeccion"
              WHERE "OperadorId"={operatorId} ORDER BY "Tipo","EntidadId" LIMIT 5001)r
            """).SingleAsync(ct);
        var rows = JsonSerializer.Deserialize<Row[]>(photograph.Filas, Json)!;
        if (rows.Length > 5000) throw new InvalidOperationException("The board exceeds its configured query limit.");
        var status = photograph.Estado;
        var now = clock.GetUtcNow();
        var entities = rows.Select(x => x.Evento.Deserialize<EventoOperativo>(Json)!).ToArray();
        var risks = entities.Where(x => x.Tipo == "Envio").Select(shipment =>
        {
            var state = shipment.Datos.TryGetProperty("estado", out var stateValue) ? stateValue.GetString() : null;
            var final = state is "Entregado" or "EntregadoComercio" or "Cancelado" or "Devuelto";
            var deadline = shipment.Datos.TryGetProperty("compromisoHastaUtc", out var value) && value.ValueKind == JsonValueKind.String ? value.GetDateTimeOffset() : (DateTimeOffset?)null;
            var stop = entities.FirstOrDefault(x => x.Tipo == "Parada" && x.EnvioId == shipment.EntidadId
                && x.Datos.TryGetProperty("activa", out var active) && active.ValueKind == JsonValueKind.True);
            var allowsArrival = state is "AsignadoRuta" or "EnTransito" or "EntregadoParcialmente" or "RetiroAsignado";
            var arrival = allowsArrival && stop is not null && stop.Datos.TryGetProperty("llegadaEstimadaUtc", out var planned) && planned.ValueKind == JsonValueKind.String ? planned.GetDateTimeOffset() : (DateTimeOffset?)null;
            return new RiesgoEnvio(shipment.EntidadId, EvaluadorRiesgoOperativo.Evaluar(now, status == "Actualizada", final, deadline, arrival).ToString());
        }).ToArray();
        return new(rows.Select(x => x.Revision).DefaultIfEmpty(0).Max(), now, status, entities) { Riesgos = risks };
    }
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record Photograph(string Filas, string Estado);
    private sealed record Row(long Revision, JsonElement Evento);
}
