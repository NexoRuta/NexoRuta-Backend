using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Gestion;
using NexoRuta.Infrastructure.Persistence;
namespace NexoRuta.Infrastructure.Gestion;

public sealed class EfGestionRepository(NexoRutaDbContext db) : IGestionRepository
{
    public async Task<OpcionesGestion> OpcionesAsync(Guid operador, string tipo, string buscar, int pagina, Guid[] seleccionado, CancellationToken ct)
    {
        await db.SelectOperatorAsync(operador, ct);
        // All sources retain their RLS policies. No active-only filter: historical queries need disabled entities.
        var source = tipo switch
        {
            "Comercio" => """
                SELECT c."Id",c."Nombre" || ' · ' || c."Estado" || CASE WHEN count(*) OVER(PARTITION BY c."Nombre")>1 THEN
                ' · Envío ' || coalesce((SELECT min(b."Codigo") FROM public."Bultos" b JOIN public."Envios" e ON e."Id"=b."EnvioId" WHERE e."ComercioId"=c."Id" AND e."OperadorId"={0}),'sin referencia') ELSE '' END AS "Nombre" FROM public."Comercios" c
                WHERE EXISTS(SELECT 1 FROM public."Envios" e WHERE e."OperadorId"={0} AND e."ComercioId"=c."Id")
                """,
            "Zona" => """
                SELECT "Id","Nombre" || ' · ' || "Estado" || CASE WHEN count(*) OVER(PARTITION BY "Nombre")>1
                THEN ' · Creada ' || to_char("CreadaUtc" AT TIME ZONE 'America/Montevideo','DD/MM/YYYY HH24:MI:SS.US') ELSE '' END AS "Nombre"
                FROM public."ZonasCobertura" WHERE "OperadorId"={0}
                """,
            "Repartidor" => """
                SELECT r."AccesoId" AS "Id",r."Nombre" || CASE WHEN count(*) OVER(PARTITION BY r."Nombre")>1 THEN ' · ' || coalesce((SELECT u."Email" FROM public."AccesosUsuario" a JOIN public."Usuarios" u ON u."Id"=a."UsuarioId" WHERE a."Id"=r."AccesoId"),'Sin contacto') ELSE '' END || CASE WHEN r."Activo" THEN '' ELSE ' · Inactivo' END AS "Nombre"
                FROM public."Repartidores" r WHERE r."OperadorId"={0}
                """,
            "Envio" => """
                SELECT e."Id",(SELECT min(b."Codigo") FROM public."Bultos" b WHERE b."EnvioId"=e."Id") AS "Nombre"
                FROM public."Envios" e WHERE e."OperadorId"={0}
                """,
            "Ruta" => """
                SELECT "Id",to_char("Fecha",'DD/MM/YYYY') || ' · ' || "VehiculoMatricula" AS "Nombre" FROM public."HojasRuta" WHERE "OperadorId"={0}
                """,
            _ => """
                SELECT "Id","Codigo" AS "Nombre" FROM public."Bultos" WHERE "OperadorId"={0}
                """
        };
        var rows = await db.Database.SqlQueryRaw<OpcionGestion>("SELECT * FROM (" + source + """
            ) o WHERE (cardinality({3}::uuid[])>0 AND "Id"=ANY({3})) OR (cardinality({3}::uuid[])=0 AND strpos(lower("Nombre"),lower({1}))>0)
            ORDER BY "Nombre","Id" OFFSET {2} LIMIT 101
            """,operador,buscar,(pagina-1)*50,seleccionado).ToArrayAsync(ct);
        var size=seleccionado.Length>0?100:50;
        return new(rows.Take(size).ToArray(),rows.Length>size);
    }
    private const string Scope = """
        g."OperadorId"={0} AND ({3}::uuid IS NULL OR g."ComercioId"={3})
        AND ({4}::uuid IS NULL OR g."ZonaId"={4})
        AND ({5}::uuid IS NULL OR EXISTS(SELECT 1 FROM public."GestionBultos" b WHERE b."OperadorId"=g."OperadorId" AND b."EnvioId"=g."EnvioId" AND b."RepartidorId"={5}))
        """;
    private const string Source = """
        WITH s AS(SELECT g.*,nexoruta_app.management_compliance(g."CompromisoHastaUtc",g."CompletadaEnUtc",g."EnFranja",g."PruebaPendiente",g."Estado" IN ('Cancelado','EnDevolucion','Devuelto','Extraviado')) c
        FROM public."GestionOperaciones" g WHERE
        """ + " " + Scope + " AND g.\"AdmitidaEnUtc\">={1} AND g.\"AdmitidaEnUtc\"<{2}) ";
    private object[] Args(FiltroGestion f) => [f.OperadorId, f.Desde.ToUniversalTime(), f.Hasta.ToUniversalTime(),
        (object?)f.ComercioId ?? DBNull.Value, (object?)f.ZonaId ?? DBNull.Value,
        (object?)f.RepartidorId ?? DBNull.Value, (f.Pagina - 1) * f.Tamano, f.Tamano];
    public async Task<ReporteGestion> ReporteAsync(FiltroGestion f, CancellationToken ct)
    {
        var a = Args(f);
        var indicadores = await db.Database.SqlQueryRaw<Indicador>(Source + """
            SELECT d.corte AS "Corte",d.clave AS "Clave",'operaciones'::text AS "Unidad",count(*)::int AS "Cantidad",
            count(*) FILTER(WHERE c='Cumplido')::int AS "Cumplidos",count(*) FILTER(WHERE c='Incumplido')::int AS "Incumplidos",
            count(*) FILTER(WHERE c='SinCompromiso')::int AS "SinCompromiso",count(*) FILTER(WHERE c='EnPlazo')::int AS "EnPlazo",
            count(*) FILTER(WHERE c='PruebaPendiente')::int AS "PruebaPendiente",count(*) FILTER(WHERE c='NoEvaluable')::int AS "NoEvaluables",
            sum("Bultos")::int AS "Bultos",sum("PesoKg") AS "PesoKg",sum("VolumenM3") AS "VolumenM3",
            sum("Entregados")::int AS "Entregados",sum("Pendientes")::int AS "Pendientes",sum("Devueltos")::int AS "Devueltos"
            FROM s CROSS JOIN LATERAL(VALUES('Zona',coalesce("ZonaId"::text,'Sin zona')),('Comercio',"ComercioId"::text),('Tipo',CASE WHEN "EsRetiro" THEN 'Retiro' ELSE 'Entrega' END),('Resultado',"Estado"))d(corte,clave)
            GROUP BY d.corte,d.clave ORDER BY d.corte,d.clave
            """, a).ToListAsync(ct);
        var repartidores = await db.Database.SqlQueryRaw<Indicador>(Source + """
            , parcels AS(SELECT b.*,nexoruta_app.management_compliance(s."CompromisoHastaUtc",b."EntregadoEnUtc",b."EnFranja",b."Resultado" IN ('EntregaPendienteValidacion','EntregaSinValidacion'),false) pc
            FROM s JOIN public."GestionBultos" b ON b."OperadorId"=s."OperadorId" AND b."EnvioId"=s."EnvioId" WHERE b."EntregadoEnUtc" IS NOT NULL AND ({5}::uuid IS NULL OR b."RepartidorId"={5}))
            SELECT 'Repartidor'::text AS "Corte",coalesce("RepartidorId"::text,'Sin autor verificable') AS "Clave",'bultos entregados físicamente / retirados'::text AS "Unidad",count(*)::int AS "Cantidad",
            count(*) FILTER(WHERE pc='Cumplido')::int AS "Cumplidos",count(*) FILTER(WHERE pc='Incumplido')::int AS "Incumplidos",count(*) FILTER(WHERE pc='SinCompromiso')::int AS "SinCompromiso",0::int AS "EnPlazo",count(*) FILTER(WHERE pc='PruebaPendiente')::int AS "PruebaPendiente",0::int AS "NoEvaluables",count(*)::int AS "Bultos",0::numeric AS "PesoKg",0::numeric AS "VolumenM3",count(*) FILTER(WHERE "Resultado" IN ('Entregado','EntregadoComercio'))::int AS "Entregados",0::int AS "Pendientes",0::int AS "Devueltos"
            FROM parcels GROUP BY "RepartidorId" ORDER BY "RepartidorId"
            """, a).ToArrayAsync(ct);
        indicadores.AddRange(repartidores);
        var total = await db.Database.SqlQueryRaw<int>(Source + "SELECT count(*)::int AS \"Value\" FROM s", a).SingleAsync(ct);
        var detalles = await db.Database.SqlQueryRaw<DetalleOperacion>(Source + """
            SELECT "EnvioId","ComercioId","ZonaId","Zona","EsRetiro","Estado","AdmitidaEnUtc","CompromisoHastaUtc","CompletadaEnUtc",c AS "Cumplimiento","Bultos","PesoKg","VolumenM3"
            FROM s ORDER BY "AdmitidaEnUtc","EnvioId" OFFSET {6} LIMIT {7}
            """, a).ToArrayAsync(ct);
        const string reasons = "WITH r AS(SELECT m.* FROM public.\"GestionMotivos\" m JOIN public.\"GestionOperaciones\" g ON g.\"OperadorId\"=m.\"OperadorId\" AND g.\"EnvioId\"=m.\"EnvioId\" WHERE " + "g.\"OperadorId\"={0} AND ({3}::uuid IS NULL OR g.\"ComercioId\"={3}) AND ({4}::uuid IS NULL OR g.\"ZonaId\"={4})" + " AND m.\"OcurridaEnUtc\">={1} AND m.\"OcurridaEnUtc\"<{2} AND ({5}::uuid IS NULL OR m.\"RepartidorId\"={5})) ";
        var motivos = await db.Database.SqlQueryRaw<MotivoGestion>(reasons + """
            SELECT "Tipo","Motivo",CASE WHEN "Tipo"='PendienteBulto' THEN 'resultados de bulto pendiente por visita' ELSE 'intentos' END AS "Unidad",
            CASE WHEN "Tipo"='PendienteBulto' THEN sum("Bultos") ELSE count(*) END::int AS "Cantidad" FROM r GROUP BY "Tipo","Motivo" ORDER BY "Tipo","Motivo"
            """, a).ToArrayAsync(ct);
        var motivosDetalle = await db.Database.SqlQueryRaw<DetalleMotivo>(reasons + """
            SELECT "OperacionId","EnvioId","RepartidorId","RutaId","OcurridaEnUtc","Tipo","Motivo","Bultos" FROM r
            ORDER BY "OcurridaEnUtc","OperacionId","Tipo","Motivo" OFFSET {6} LIMIT {7}
            """, a).ToArrayAsync(ct);
        var totalMotivos = await db.Database.SqlQueryRaw<int>(reasons + "SELECT count(*)::int AS \"Value\" FROM r", a).SingleAsync(ct);
        var health = await db.Database.SqlQuery<Health>($"""
            SELECT statement_timestamp() AS "Now",max("ActualizadaEnUtc") AS "Updated",count(*) FILTER(WHERE "Generacion"<>"Aplicada")::int AS "Pending",
            CASE WHEN NOT EXISTS(SELECT 1 FROM public."OperacionProyeccionSalud" WHERE "Proyeccion"='management' HAVING count(*)=max("Particiones") AND min("VerificadaEnUtc")>statement_timestamp()-interval '15 seconds') THEN 'Indisponible'
            WHEN count(*) FILTER(WHERE "Generacion"<>"Aplicada")>0 THEN 'Atrasada' ELSE 'Actualizada' END AS "Status"
            FROM public."GestionPendientes" WHERE "OperadorId"={f.OperadorId}
            """).SingleAsync(ct);
        return new(health.Now, health.Updated, health.Pending, health.Status, indicadores, motivos, total, detalles, motivosDetalle, totalMotivos);
    }
    public async Task<DetalleGestion?> DetalleAsync(Guid operador, Guid envio, CancellationToken ct)
    {
        var operation=await db.Database.SqlQuery<DetalleOperacion>($"""
            SELECT "EnvioId","ComercioId","ZonaId","Zona","EsRetiro","Estado","AdmitidaEnUtc","CompromisoHastaUtc","CompletadaEnUtc",
            nexoruta_app.management_compliance("CompromisoHastaUtc","CompletadaEnUtc","EnFranja","PruebaPendiente","Estado" IN ('Cancelado','EnDevolucion','Devuelto','Extraviado')) AS "Cumplimiento","Bultos","PesoKg","VolumenM3"
            FROM public."GestionOperaciones" WHERE "OperadorId"={operador} AND "EnvioId"={envio}
            """).SingleOrDefaultAsync(ct);
        if(operation is null)return null;
        var parcels=await db.Database.SqlQuery<DetalleBultoGestion>($"""
            SELECT "BultoId","RepartidorId","RutaId","EntregadoEnUtc","Resultado","EnFranja"
            FROM public."GestionBultos" WHERE "OperadorId"={operador} AND "EnvioId"={envio} ORDER BY "BultoId"
            """).ToArrayAsync(ct);
        return new(operation,parcels);
    }
    private sealed record Health(DateTimeOffset Now, DateTimeOffset? Updated, int Pending, string Status);
    public async Task<CuentaGestion> CuentaAsync(FiltroGestion f, Guid comercio, CancellationToken ct)
    {
        await db.SelectOperatorAsync(f.OperadorId, ct);
        var totals = await db.Database.SqlQuery<Totals>($"""
            SELECT coalesce(sum(m."ImporteUyu"),0) AS "Balance",
            coalesce(sum(m."ImporteUyu") FILTER(WHERE m."FechaUtc">={f.Desde.ToUniversalTime()} AND m."FechaUtc"<{f.Hasta.ToUniversalTime()}),0) AS "Period",
            coalesce(sum(m."ImporteUyu") FILTER(WHERE m."Tratamiento"='Liquidable' AND c."MovimientoId" IS NULL),0) AS "Pending",
            count(*) FILTER(WHERE m."FechaUtc">={f.Desde.ToUniversalTime()} AND m."FechaUtc"<{f.Hasta.ToUniversalTime()})::int AS "Count"
            FROM public."MovimientosCuenta" m LEFT JOIN public."CargosLiquidados" c ON c."MovimientoId"=m."Id"
            WHERE m."OperadorId"={f.OperadorId} AND m."ComercioId"={comercio}
            """).SingleAsync(ct);
        var rows = await db.Database.SqlQuery<MovimientoCuenta>($"""
            SELECT m."Id",m."EnvioId",m."FechaUtc",m."Concepto",m."ImporteUyu",m."TotalPersistidoUyu",m."Tratamiento",c."LiquidacionId"
            FROM public."MovimientosCuenta" m LEFT JOIN public."CargosLiquidados" c ON c."MovimientoId"=m."Id"
            WHERE m."OperadorId"={f.OperadorId} AND m."ComercioId"={comercio} AND m."FechaUtc">={f.Desde.ToUniversalTime()} AND m."FechaUtc"<{f.Hasta.ToUniversalTime()}
            ORDER BY m."FechaUtc",m."Id" OFFSET {(f.Pagina - 1) * f.Tamano} LIMIT {f.Tamano}
            """).ToArrayAsync(ct);
        var docs = await db.Database.SqlQuery<LiquidacionResumen>($"""
            SELECT "Id","ComercioId","Estado","DesdeUtc","HastaUtc","CorteUtc","TotalUyu","PreparadaPor","PreparadaEnUtc","ConfirmadaPor","ConfirmadaEnUtc"
            FROM public."Liquidaciones" WHERE "OperadorId"={f.OperadorId} AND "ComercioId"={comercio}
            ORDER BY "PreparadaEnUtc" DESC,"Id" DESC OFFSET {(f.Pagina - 1) * f.Tamano} LIMIT {f.Tamano}
            """).ToArrayAsync(ct);
        return new(f.OperadorId, comercio, totals.Balance, totals.Period, totals.Pending, totals.Count, rows, docs);
    }
    public Task<Guid> CorregirAsync(CorregirCuenta c, CancellationToken ct)
        => db.Database.SqlQuery<Guid>($"SELECT nexoruta_app.correct_settled_movement({c.Id},{c.MovimientoOriginalId},{c.ImporteUyu},{c.Motivo}) AS \"Value\"").SingleAsync(ct);
    private sealed record Totals(decimal Balance, decimal Period, decimal Pending, int Count);
    public async Task<LiquidacionDetalle?> LiquidacionAsync(Guid operador, Guid id, CancellationToken ct)
    {
        await db.SelectOperatorAsync(operador, ct);
        var doc = await db.Database.SqlQuery<LiquidacionResumen>($"""
            SELECT "Id","ComercioId","Estado","DesdeUtc","HastaUtc","CorteUtc","TotalUyu","PreparadaPor","PreparadaEnUtc","ConfirmadaPor","ConfirmadaEnUtc" FROM public."Liquidaciones" WHERE "OperadorId"={operador} AND "Id"={id}
            """).SingleOrDefaultAsync(ct);
        if (doc is null) return null;
        // Document details are bounded at preparation, independent of future tariff/state changes.
        var lines = await db.Database.SqlQuery<LineaLiquidacion>($"""
            SELECT "MovimientoId","EnvioId","FechaUtc","Concepto","ImporteUyu","Fuente" FROM public."LiquidacionLineas" WHERE "OperadorId"={operador} AND "LiquidacionId"={id} ORDER BY "FechaUtc","MovimientoId"
            """).ToArrayAsync(ct);
        var transitions=await db.Database.SqlQuery<TransicionLiquidacion>($"""
            SELECT "Estado","ActorId","FechaUtc" FROM public."LiquidacionTransiciones" WHERE "OperadorId"={operador} AND "LiquidacionId"={id} ORDER BY "FechaUtc","Id"
            """).ToArrayAsync(ct);
        return new(doc, lines, transitions);
    }
    public async Task<LiquidacionDetalle> PrepararAsync(Guid operador, Guid actor, PrepararLiquidacion c, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT nexoruta_app.prepare_settlement({c.Id},{c.ComercioId},{c.DesdeUtc.ToUniversalTime()},{c.HastaUtc.ToUniversalTime()})", ct);
        return (await LiquidacionAsync(operador, c.Id, ct))!;
    }
    public async Task<LiquidacionDetalle?> TransicionAsync(Guid operador, Guid actor, Guid id, bool confirmar, CancellationToken ct)
    {
        var found = await db.Database.SqlQuery<bool>($"SELECT nexoruta_app.transition_settlement({id},{confirmar}) AS \"Value\"").SingleAsync(ct);
        return found ? await LiquidacionAsync(operador, id, ct) : null;
    }
}
