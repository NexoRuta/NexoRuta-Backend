using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Application.Envios.Devoluciones;
using NexoRuta.Application.Reparto.Exceptions;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Envios;
using NexoRuta.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace NexoRuta.Infrastructure.Envios.Repositories;

public sealed class EfDevolucionesRepository(
    NexoRutaDbContext db,
    TimeProvider reloj,
    IPermisosUsuarioRepository permisos) : IDevolucionesRepository
{
    private static readonly JsonSerializerOptions Json = new();

    public async Task<PaginaDevolucionesDatos> ListarAsync(ContextoUsuario actor, Guid? operadorId,
        DateTimeOffset desdeUtc, DateTimeOffset hastaUtc, string? situacion, string? referencia,
        string? cursor, int limite, CancellationToken cancellationToken)
    {
        await using var tx = await db.BeginOrJoinAsync(cancellationToken);
        await ContextualizarAsync(actor, operadorId, cancellationToken);
        var cursorData = LeerCursor(cursor, actor, operadorId, desdeUtc, hastaUtc, situacion, referencia);
        var rows = await db.Database.SqlQueryRaw<DevolucionResumenFila>("""
            WITH candidatos AS (
                SELECT e."Id" AS "EnvioId", e."OperadorId", e."ComercioId", e."Estado"::text AS "EstadoEnvio",
                    e."CreadoEnUtc", o."Nombre" AS "OperadorNombre",
                    COALESCE(i."ReferenciaExterna", left(e."Id"::text, 8)) AS "Referencia",
                    b."Id" AS "BultoId", b."Resultado"::text AS "Resultado", b."Custodia"::text AS "Custodia",
                    t."Id" AS "TransferenciaId", t."RegistradaEnUtc" AS "TransferidaEnUtc",
                    al."AcuseId", a."Resultado" AS "ResultadoAcuse",
                    ev."ActividadRetornoUtc",
                    GREATEST(e."CreadoEnUtc", COALESCE(t."RegistradaEnUtc", e."CreadoEnUtc"), COALESCE(ev."ActividadRetornoUtc", e."CreadoEnUtc")) AS "ActividadFila"
                FROM public."Envios" e
                JOIN public."Operadores" o ON o."Id"=e."OperadorId"
                JOIN public."Bultos" b ON b."OperadorId"=e."OperadorId" AND b."EnvioId"=e."Id"
                LEFT JOIN LATERAL nexoruta_app.list_import_shipment_references(ARRAY[e."Id"]) i ON i."EnvioId"=e."Id"
                LEFT JOIN public."TransferenciasDevolucionBultos" tl ON tl."OperadorId"=b."OperadorId" AND tl."EnvioId"=b."EnvioId" AND tl."BultoId"=b."Id"
                LEFT JOIN public."TransferenciasDevolucion" t ON t."Id"=tl."TransferenciaId"
                LEFT JOIN public."AcusesDevolucionBultos" al ON al."TransferenciaId"=tl."TransferenciaId" AND al."BultoId"=b."Id"
                LEFT JOIN public."AcusesDevolucionComercio" a ON a."Id"=al."AcuseId"
                LEFT JOIN LATERAL (
                    SELECT max(h."OcurridaEnUtc") AS "ActividadRetornoUtc"
                    FROM public."EnvioEventos" h
                    WHERE h."OperadorId"=e."OperadorId" AND h."EnvioId"=e."Id"
                        AND (h."OrigenTipo" IN ('DecisionDevolucionEnvio','DevolucionBultosComercio')
                            OR (h."OrigenTipo"='RecepcionDeposito' AND h."EstadoNuevo" IN ('Devuelto','EnDevolucion')))
                ) ev ON true
                WHERE NOT e."EsRetiro" AND e."ComercioId"=@commerceId
                    AND (@operatorId IS NULL OR e."OperadorId"=@operatorId)
                    AND (b."Resultado"::text IN ('EnDevolucion','Devuelto') OR tl."TransferenciaId" IS NOT NULL OR ev."ActividadRetornoUtc" IS NOT NULL)
            ), resumen AS (
                SELECT c."EnvioId", c."OperadorId", c."OperadorNombre", c."Referencia", c."EstadoEnvio",
                    GREATEST(c."CreadoEnUtc", COALESCE(max(c."TransferidaEnUtc"),c."CreadoEnUtc"), COALESCE(max(c."ActividadRetornoUtc"),c."CreadoEnUtc")) AS "ActividadEnUtc",
                    count(*)::integer AS "TotalBultos",
                    count(*) FILTER (WHERE c."Resultado"='EnDevolucion' AND c."Custodia"<>'Deposito')::integer AS "EnRetorno",
                    count(*) FILTER (WHERE c."Resultado" IN ('EnDevolucion','Devuelto') AND c."Custodia"='Deposito')::integer AS "EnDeposito",
                    count(*) FILTER (WHERE c."TransferenciaId" IS NOT NULL AND c."AcuseId" IS NULL)::integer AS "PendientesAcuse",
                    count(*) FILTER (WHERE c."ResultadoAcuse"='ConObservaciones')::integer AS "RecibidosConObservaciones",
                    count(*) FILTER (WHERE c."TransferenciaId" IS NOT NULL)::integer AS "Transferidos"
                FROM candidatos c
                GROUP BY c."EnvioId",c."OperadorId",c."OperadorNombre",c."Referencia",c."EstadoEnvio",c."CreadoEnUtc"
            ), con_situacion AS (
                SELECT r.*, CASE WHEN r."PendientesAcuse">0 THEN 'PendienteAcuse'
                    WHEN r."EnDeposito">0 THEN 'EnDeposito' WHEN r."EnRetorno">0 THEN 'EnRetorno'
                    WHEN r."Transferidos">0 THEN 'Recibido' ELSE 'Historico' END AS "Situacion"
                FROM resumen r
            )
            SELECT s."EnvioId", s."OperadorId", s."OperadorNombre", s."Referencia", s."EstadoEnvio", s."ActividadEnUtc",
                s."TotalBultos",s."EnRetorno",s."EnDeposito",s."PendientesAcuse",s."RecibidosConObservaciones",s."Situacion"
            FROM con_situacion s
            WHERE s."ActividadEnUtc">=@fromUtc AND s."ActividadEnUtc"<@toUtc
                AND (@situation IS NULL OR s."Situacion"=@situation)
                AND (@reference IS NULL OR s."Referencia" ILIKE '%'||@reference||'%')
                AND (@cursorDate IS NULL OR s."ActividadEnUtc"<@cursorDate OR (s."ActividadEnUtc"=@cursorDate AND s."EnvioId"<@cursorShipment))
            ORDER BY s."ActividadEnUtc" DESC,s."EnvioId" DESC
            LIMIT @fetchLimit
            """, Param("commerceId", NpgsqlDbType.Uuid, actor.ComercioId!.Value),
            Param("operatorId", NpgsqlDbType.Uuid, operadorId),
            Param("fromUtc", NpgsqlDbType.TimestampTz, desdeUtc), Param("toUtc", NpgsqlDbType.TimestampTz, hastaUtc),
            Param("situation", NpgsqlDbType.Text, situacion), Param("reference", NpgsqlDbType.Text, referencia),
            Param("cursorDate", NpgsqlDbType.TimestampTz, cursorData?.ActividadEnUtc),
            Param("cursorShipment", NpgsqlDbType.Uuid, cursorData?.EnvioId), Param("fetchLimit", NpgsqlDbType.Integer, limite + 1))
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > limite;
        var pageRows = rows.Take(limite).ToArray();
        var next = hasMore && pageRows.Length > 0
            ? EscribirCursor(new(actor.ComercioId.Value, operadorId, desdeUtc, hastaUtc, situacion, referencia,
                pageRows[^1].ActividadEnUtc, pageRows[^1].EnvioId))
            : null;
        var items = pageRows.Select(x => new DevolucionResumenDatos(x.EnvioId, x.OperadorId, x.OperadorNombre,
            x.Referencia, x.EstadoEnvio, x.ActividadEnUtc, x.TotalBultos, x.EnRetorno, x.EnDeposito,
            x.PendientesAcuse, x.RecibidosConObservaciones, x.Situacion)).ToArray();
        await tx.CommitAsync(cancellationToken);
        return new(items, next);
    }

    public async Task<DevolucionDetalleDatos?> ObtenerAsync(ContextoUsuario actor, Guid operadorId, Guid envioId,
        int desdeBulto, int limite, bool puedeAcusar, CancellationToken cancellationToken)
    {
        await using var tx = await db.BeginOrJoinAsync(cancellationToken);
        await ContextualizarAsync(actor, operadorId, cancellationToken);
        var rows = await db.Database.SqlQueryRaw<DevolucionDetalleFila>("""
            WITH envio AS (
                SELECT e."Id" AS "EnvioId",e."OperadorId",e."ComercioId",e."Estado"::text AS "EstadoEnvio",e."Version" AS "VersionEnvio",
                    e."EsRetiro",o."Nombre" AS "OperadorNombre",COALESCE(i."ReferenciaExterna",left(e."Id"::text,8)) AS "Referencia"
                FROM public."Envios" e JOIN public."Operadores" o ON o."Id"=e."OperadorId"
                LEFT JOIN LATERAL nexoruta_app.list_import_shipment_references(ARRAY[e."Id"]) i ON i."EnvioId"=e."Id"
                WHERE e."Id"=@shipmentId AND e."OperadorId"=@operatorId AND e."ComercioId"=@commerceId AND NOT e."EsRetiro"
                    AND (EXISTS(SELECT 1 FROM public."Bultos" b WHERE b."OperadorId"=e."OperadorId" AND b."EnvioId"=e."Id" AND b."Resultado"::text IN ('EnDevolucion','Devuelto'))
                        OR EXISTS(SELECT 1 FROM public."TransferenciasDevolucion" t WHERE t."OperadorId"=e."OperadorId" AND t."EnvioId"=e."Id")
                        OR EXISTS(SELECT 1 FROM public."EnvioEventos" h WHERE h."OperadorId"=e."OperadorId" AND h."EnvioId"=e."Id"
                            AND (h."OrigenTipo" IN ('DecisionDevolucionEnvio','DevolucionBultosComercio')
                                OR (h."OrigenTipo"='RecepcionDeposito' AND h."EstadoNuevo" IN ('Devuelto','EnDevolucion')))))
            ), bultos AS (
                SELECT b."Id",b."Codigo",b."Resultado"::text AS "Resultado",b."Custodia"::text AS "Custodia",b."Version",
                    tl."TransferenciaId",t."RegistradaEnUtc" AS "TransferidaEnUtc",al."AcuseId",a."Resultado" AS "ResultadoAcuse",
                    al."Observacion",a."RegistradoEnUtc" AS "AcusadoEnUtc",
                    count(*) OVER()::integer AS "TotalBultos"
                FROM envio e JOIN public."Bultos" b ON b."OperadorId"=e."OperadorId" AND b."EnvioId"=e."EnvioId"
                LEFT JOIN public."TransferenciasDevolucionBultos" tl ON tl."OperadorId"=b."OperadorId" AND tl."EnvioId"=b."EnvioId" AND tl."BultoId"=b."Id"
                LEFT JOIN public."TransferenciasDevolucion" t ON t."Id"=tl."TransferenciaId"
                LEFT JOIN public."AcusesDevolucionBultos" al ON al."TransferenciaId"=tl."TransferenciaId" AND al."BultoId"=b."Id"
                LEFT JOIN public."AcusesDevolucionComercio" a ON a."Id"=al."AcuseId"
                ORDER BY b."Id" OFFSET @offset LIMIT @limit
            )
            SELECT e."EnvioId",e."OperadorId",e."OperadorNombre",e."Referencia",e."EstadoEnvio",e."VersionEnvio",e."EsRetiro",
                (SELECT count(*)::integer FROM envio x JOIN public."Bultos" p ON p."OperadorId"=x."OperadorId" AND p."EnvioId"=x."EnvioId") AS "TotalBultos",
                (SELECT count(*)::integer FROM public."TransferenciasDevolucionBultos" tl JOIN public."TransferenciasDevolucion" t ON t."Id"=tl."TransferenciaId" LEFT JOIN public."AcusesDevolucionBultos" al ON al."TransferenciaId"=tl."TransferenciaId" AND al."BultoId"=tl."BultoId" WHERE t."OperadorId"=e."OperadorId" AND t."EnvioId"=e."EnvioId" AND al."AcuseId" IS NULL) AS "PendientesAcuse",
                b."Id" AS "BultoId",b."Codigo",b."Resultado",b."Custodia",b."Version" AS "VersionBulto",b."TransferenciaId",b."TransferidaEnUtc",b."AcuseId",b."ResultadoAcuse",b."Observacion",b."AcusadoEnUtc"
            FROM envio e LEFT JOIN bultos b ON true
            ORDER BY b."Id"
            """, Param("shipmentId", NpgsqlDbType.Uuid, envioId), Param("operatorId", NpgsqlDbType.Uuid, operadorId),
            Param("commerceId", NpgsqlDbType.Uuid, actor.ComercioId!.Value), Param("offset", NpgsqlDbType.Integer, desdeBulto),
            Param("limit", NpgsqlDbType.Integer, limite)).ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            await tx.CommitAsync(cancellationToken);
            return null;
        }

        var first = rows[0];
        var bultos = rows.Where(x => x.BultoId.HasValue).Select(x => ProyectarBulto(x, puedeAcusar)).ToArray();
        await tx.CommitAsync(cancellationToken);
        return new(first.EnvioId, first.OperadorId, first.OperadorNombre, first.Referencia, first.EstadoEnvio,
            first.VersionEnvio, first.EsRetiro, first.TotalBultos, first.PendientesAcuse, bultos);
    }

    public async Task<IReadOnlyList<HechoDevolucionDatos>?> HistorialAsync(ContextoUsuario actor, Guid operadorId,
        Guid envioId, CancellationToken cancellationToken)
    {
        await using var tx = await db.BeginOrJoinAsync(cancellationToken);
        await ContextualizarAsync(actor, operadorId, cancellationToken);
        if (!await db.Envios.AsNoTracking().AnyAsync(e => e.Id == envioId && e.OperadorId == operadorId && !e.EsRetiro, cancellationToken))
        {
            await tx.CommitAsync(cancellationToken);
            return null;
        }
        var rows = await db.Database.SqlQueryRaw<HechoDevolucionFila>("""
            SELECT h."Id",h."OrigenTipo" AS "Tipo",h."RegistradaEnUtc" AS "RegistradoEnUtc",h."OcurridaEnUtc" AS "OcurridoEnUtc",
                CASE WHEN h."ActorTipo"='Usuario' THEN 'Operador' ELSE h."ActorTipo" END AS "Actor",
                NULL::integer AS "Bultos",h."CodigoHecho" AS "Resultado",NULL::text AS "Observaciones"
            FROM public."EnvioEventos" h WHERE h."OperadorId"=@operatorId AND h."EnvioId"=@shipmentId
                AND (h."OrigenTipo" IN ('DecisionDevolucionEnvio','DevolucionBultosComercio')
                    OR (h."OrigenTipo"='RecepcionDeposito' AND h."EstadoNuevo" IN ('Devuelto','EnDevolucion')))
            UNION ALL
            SELECT t."Id",'Transferencia fisica al comercio',t."RegistradaEnUtc",t."RegistradaEnUtc",'Operador',count(l."BultoId")::integer,'Transferida',NULL::text
            FROM public."TransferenciasDevolucion" t JOIN public."TransferenciasDevolucionBultos" l ON l."TransferenciaId"=t."Id"
            WHERE t."OperadorId"=@operatorId AND t."EnvioId"=@shipmentId GROUP BY t."Id",t."RegistradaEnUtc"
            UNION ALL
            SELECT a."Id",'Acuse del comercio',a."RegistradoEnUtc",a."RegistradoEnUtc",'Comercio',count(l."BultoId")::integer,a."Resultado",NULL::text
            FROM public."AcusesDevolucionComercio" a JOIN public."AcusesDevolucionBultos" l ON l."AcuseId"=a."Id"
            WHERE a."OperadorId"=@operatorId AND a."EnvioId"=@shipmentId GROUP BY a."Id",a."RegistradoEnUtc",a."Resultado"
            ORDER BY "RegistradoEnUtc","Id"
            """, Param("operatorId", NpgsqlDbType.Uuid, operadorId), Param("shipmentId", NpgsqlDbType.Uuid, envioId))
            .ToListAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return rows.Select(x => new HechoDevolucionDatos(x.Id, x.Tipo, x.RegistradoEnUtc, x.OcurridoEnUtc,
            x.Actor, x.Bultos, x.Resultado, x.Observaciones)).ToArray();
    }

    public async Task<TransferenciaDevolucionResultadoDatos?> TransferirAsync(ContextoUsuario actor, Guid envioId,
        TransferirDevolucionComando comando, CancellationToken cancellationToken)
    {
        var operadorId = actor.OperadorId ?? throw new AccesoNoPermitidoException();
        var bultosCommand = comando.Bultos.OrderBy(x => x.BultoId).ToArray();
        var huella = HuellaTransferencia(actor, envioId, comando, bultosCommand);
        await using var tx = await db.BeginOrJoinAsync(cancellationToken);
        await db.ApplyAsync(RlsActorContext.From(actor), cancellationToken);
        if (!await permisos.TienePermisoAsync(actor.UsuarioId, actor.AccesoId, "envios.gestionar", cancellationToken))
            throw new AccesoNoPermitidoException();
        await BloquearOperadorAsync(operadorId, cancellationToken);

        var existing = await LeerTransferenciaAsync(operadorId, comando.ComandoId, cancellationToken);
        if (existing is not null)
        {
            ExigirMismoComando(existing.AccesoId, existing.UsuarioId, existing.HuellaSolicitud, actor, huella);
            var result = new TransferenciaDevolucionResultadoDatos(existing.Id, existing.CantidadBultos, existing.RegistradaEnUtc);
            await tx.CommitAsync(cancellationToken);
            return result;
        }

        var shipment = await db.Envios.FromSqlInterpolated($"""
            SELECT * FROM public."Envios" WHERE "OperadorId"={operadorId} AND "Id"={envioId} FOR UPDATE
            """).SingleOrDefaultAsync(cancellationToken);
        if (shipment is null) return null;
        if (shipment.EsRetiro || shipment.Version != comando.VersionEnvio
            || shipment.Estado is EstadoEnvio.Cancelado or EstadoEnvio.Extraviado
            || shipment.Estado is not (EstadoEnvio.EnDevolucion or EstadoEnvio.Devuelto or EstadoEnvio.EntregadoParcialmente))
            throw new ConflictoRepartoException("El envío cambió o no admite una transferencia de devolución.");

        var allParcels = await db.Bultos.FromSqlInterpolated($"""
            SELECT * FROM public."Bultos" WHERE "OperadorId"={operadorId} AND "EnvioId"={envioId} ORDER BY "Id" FOR UPDATE
            """).ToArrayAsync(cancellationToken);
        var selected = allParcels.Where(b => bultosCommand.Any(x => x.BultoId == b.Id)).OrderBy(b => b.Id).ToArray();
        if (selected.Length != bultosCommand.Length)
            throw new ConflictoRepartoException("Uno o más bultos no pertenecen a este envío.");
        if (selected.Any(b => b.Custodia != EstadoCustodiaBulto.Deposito
            || b.Resultado is not (ResultadoBulto.EnDevolucion or ResultadoBulto.Devuelto)
            || bultosCommand.Single(c => c.BultoId == b.Id).VersionBulto != b.Version))
            throw new ConflictoRepartoException("Algún bulto cambió o no está bajo custodia del depósito en estado de devolución.");
        if (selected.Any(b => b.Resultado == ResultadoBulto.Devuelto)
            && !await db.EnvioEventos.AsNoTracking().AnyAsync(e => e.OperadorId == operadorId && e.EnvioId == envioId
                && (e.OrigenTipo == "DecisionDevolucionEnvio" || e.OrigenTipo == "RecepcionDeposito")
                && (e.EstadoNuevo == nameof(EstadoEnvio.Devuelto) || e.EstadoNuevo == nameof(EstadoEnvio.EnDevolucion)), cancellationToken))
            throw new ConflictoRepartoException("No hay historia canónica suficiente para transferir estos bultos históricos.");
        if (allParcels.Length == 0)
            throw new ConflictoRepartoException("El envío no tiene bultos para transferir.");

        var now = reloj.GetUtcNow().ToUniversalTime();
        var snapshots = selected.Select(b => new TransferenciaLineaInsert(b.Id, b.Version,
            checked(b.Version + (b.Resultado == ResultadoBulto.EnDevolucion ? 2 : 1)), b.Resultado.ToString(), "Deposito", "Devuelto", "Comercio")).ToArray();
        await db.SetShipmentHistoryContextAsync("DevolucionBultosComercio", comando.ComandoId, comando.ComandoId,
            now, factCode: "TransferenciaFisicaComercio", cancellationToken: cancellationToken);
        foreach (var parcel in selected)
        {
            parcel.EntregarAlComercio();
            if (parcel.Resultado == ResultadoBulto.EnDevolucion)
                parcel.RegistrarResultado(ResultadoBulto.Devuelto, parcel.Version);
        }
        shipment.ActualizarResultadoBultos(allParcels, now);
        await db.SaveChangesAsync(cancellationToken);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public."TransferenciasDevolucion" ("Id","ComandoId","OperadorId","ComercioId","EnvioId","VersionEnvioAnterior","VersionEnvioNueva","AccesoId","UsuarioId","HuellaSolicitud")
            VALUES ({comando.ComandoId},{comando.ComandoId},{operadorId},{shipment.ComercioId},{envioId},{comando.VersionEnvio},{shipment.Version},{actor.AccesoId},{actor.UsuarioId},{huella})
            """, cancellationToken);
        var lineasJson = JsonSerializer.Serialize(snapshots, Json);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public."TransferenciasDevolucionBultos" ("OperadorId","ComercioId","EnvioId","TransferenciaId","BultoId","VersionAnterior","VersionNueva","ResultadoAnterior","ResultadoNuevo","CustodiaAnterior","CustodiaNueva")
            SELECT {operadorId},{shipment.ComercioId},{envioId},{comando.ComandoId},x."BultoId",x."VersionAnterior",x."VersionNueva",x."ResultadoAnterior",x."ResultadoNuevo",x."CustodiaAnterior",x."CustodiaNueva"
            FROM jsonb_to_recordset({lineasJson}::jsonb) AS x("BultoId" uuid,"VersionAnterior" bigint,"VersionNueva" bigint,"ResultadoAnterior" text,"CustodiaAnterior" text,"ResultadoNuevo" text,"CustodiaNueva" text)
            """, cancellationToken);
        var saved = await LeerTransferenciaAsync(operadorId, comando.ComandoId, cancellationToken)
            ?? throw new InvalidOperationException("No se pudo leer la transferencia recién persistida.");
        await tx.CommitAsync(cancellationToken);
        return new(saved.Id, saved.CantidadBultos, saved.RegistradaEnUtc);
    }

    public async Task<AcuseDevolucionResultadoDatos?> AcusarAsync(ContextoUsuario actor, Guid operadorId,
        Guid transferenciaId, AcusarDevolucionComando comando, CancellationToken cancellationToken)
    {
        var comercioId = actor.ComercioId ?? throw new AccesoNoPermitidoException();
        var lineas = comando.Bultos.OrderBy(x => x.BultoId)
            .Select(x => x with { Observacion = x.Observacion?.Trim() }).ToArray();
        var huella = HuellaAcuse(actor, operadorId, transferenciaId, comando with { Bultos = lineas });
        await using var tx = await db.BeginOrJoinAsync(cancellationToken);
        await db.ApplyAsync(RlsActorContext.From(actor), cancellationToken);
        await db.SelectOperatorAsync(operadorId, cancellationToken);
        if (!await permisos.TienePermisoAsync(actor.UsuarioId, actor.AccesoId, "devoluciones.gestionar", cancellationToken))
            throw new AccesoNoPermitidoException();

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT pg_advisory_xact_lock(hashtextextended('nexoruta.return.ack-command:' || {comercioId}::text || ':' || {comando.ComandoId}::text, 0))
            """, cancellationToken);
        var existing = await LeerAcuseAsync(comercioId, comando.ComandoId, cancellationToken);
        if (existing is not null)
        {
            ExigirMismoComando(existing.AccesoId, existing.UsuarioId, existing.HuellaSolicitud, actor, huella);
            var result = new AcuseDevolucionResultadoDatos(existing.Id, existing.CantidadBultos, existing.Resultado, existing.RegistradoEnUtc);
            await tx.CommitAsync(cancellationToken);
            return result;
        }

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT pg_advisory_xact_lock(hashtextextended('nexoruta.return.ack-transfer:' || {transferenciaId}::text, 0))
            """, cancellationToken);
        var transferLines = await db.Database.SqlQueryRaw<TransferenciaBultoFila>("""
            SELECT l."BultoId",l."VersionNueva" AS "VersionBulto" FROM public."TransferenciasDevolucionBultos" l
            WHERE l."OperadorId"=@operatorId AND l."ComercioId"=@commerceId AND l."TransferenciaId"=@transferId
            """, Param("operatorId", NpgsqlDbType.Uuid, operadorId), Param("commerceId", NpgsqlDbType.Uuid, comercioId),
            Param("transferId", NpgsqlDbType.Uuid, transferenciaId)).ToListAsync(cancellationToken);
        if (transferLines.Count == 0)
        {
            await tx.CommitAsync(cancellationToken);
            return null;
        }
        var eligible = transferLines.ToDictionary(x => x.BultoId, x => x.VersionBulto);
        if (lineas.Any(x => !eligible.TryGetValue(x.BultoId, out var version) || version != x.VersionBulto))
            throw new ConflictoRepartoException("El acuse incluye bultos que no pertenecen a esta transferencia o cuyas versiones cambiaron.");
        var recibidos = await db.Database.SqlQueryRaw<Guid>("""
            SELECT l."BultoId" AS "Value" FROM public."AcusesDevolucionBultos" l
            WHERE l."TransferenciaId"=@transferId AND l."BultoId"=ANY(@parcelIds)
            """, Param("transferId", NpgsqlDbType.Uuid, transferenciaId), Param("parcelIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid, lineas.Select(x => x.BultoId).ToArray()))
            .ToArrayAsync(cancellationToken);
        if (recibidos.Length > 0)
            throw new ConflictoRepartoException("Uno o más bultos ya tienen un acuse definitivo.");

        var shipmentId = await db.Database.SqlQueryRaw<Guid>("""
            SELECT t."EnvioId" AS "Value" FROM public."TransferenciasDevolucion" t WHERE t."OperadorId"=@operatorId
                AND t."ComercioId"=@commerceId AND t."Id"=@transferId
            """, Param("operatorId", NpgsqlDbType.Uuid, operadorId), Param("commerceId", NpgsqlDbType.Uuid, comercioId),
            Param("transferId", NpgsqlDbType.Uuid, transferenciaId)).SingleOrDefaultAsync(cancellationToken);
        if (shipmentId == Guid.Empty)
        {
            await tx.CommitAsync(cancellationToken);
            return null;
        }
        var resultText = comando.Conforme ? "Conforme" : "ConObservaciones";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public."AcusesDevolucionComercio" ("Id","ComandoId","OperadorId","ComercioId","EnvioId","TransferenciaId","VersionTransferencia","Resultado","AccesoId","UsuarioId","HuellaSolicitud")
            VALUES ({comando.ComandoId},{comando.ComandoId},{operadorId},{comercioId},{shipmentId},{transferenciaId},{comando.VersionTransferencia},{resultText},{actor.AccesoId},{actor.UsuarioId},{huella})
            """, cancellationToken);
        var lineasJson = JsonSerializer.Serialize(lineas.Select(x => new AcuseLineaInsert(x.BultoId, x.VersionBulto, x.Observacion)), Json);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public."AcusesDevolucionBultos" ("OperadorId","ComercioId","EnvioId","AcuseId","TransferenciaId","BultoId","VersionBulto","Observacion")
            SELECT {operadorId},{comercioId},{shipmentId},{comando.ComandoId},{transferenciaId},x."BultoId",x."VersionBulto",x."Observacion"
            FROM jsonb_to_recordset({lineasJson}::jsonb) AS x("BultoId" uuid,"VersionBulto" bigint,"Observacion" text)
            """, cancellationToken);
        var saved = await LeerAcuseAsync(comercioId, comando.ComandoId, cancellationToken)
            ?? throw new InvalidOperationException("No se pudo leer el acuse recién persistido.");
        await tx.CommitAsync(cancellationToken);
        return new(saved.Id, saved.CantidadBultos, saved.Resultado, saved.RegistradoEnUtc);
    }

    private async Task ContextualizarAsync(ContextoUsuario actor, Guid? operadorId, CancellationToken ct)
    {
        await db.ApplyAsync(RlsActorContext.From(actor), ct);
        if (actor.TipoAcceso == TipoAccesoUsuario.Comercio && operadorId is { } selected)
            await db.SelectOperatorAsync(selected, ct);
        if (actor.TipoAcceso == TipoAccesoUsuario.Operador && operadorId is { } op && op != actor.OperadorId)
            throw new AccesoNoPermitidoException();
    }

    private Task BloquearOperadorAsync(Guid operatorId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM public.\"Operadores\" WHERE \"Id\"={operatorId} FOR UPDATE", ct);

    private async Task<TransferenciaPersistidaFila?> LeerTransferenciaAsync(Guid operatorId, Guid commandId, CancellationToken ct) =>
        await db.Database.SqlQueryRaw<TransferenciaPersistidaFila>("""
            SELECT t."Id",t."AccesoId",t."UsuarioId",t."HuellaSolicitud",t."RegistradaEnUtc",
                (SELECT count(*)::integer FROM public."TransferenciasDevolucionBultos" l WHERE l."TransferenciaId"=t."Id") AS "CantidadBultos"
            FROM public."TransferenciasDevolucion" t WHERE t."OperadorId"=@operatorId AND t."ComandoId"=@commandId
            """, Param("operatorId", NpgsqlDbType.Uuid, operatorId), Param("commandId", NpgsqlDbType.Uuid, commandId))
            .SingleOrDefaultAsync(ct);

    private async Task<AcusePersistidoFila?> LeerAcuseAsync(Guid commerceId, Guid commandId, CancellationToken ct) =>
        await db.Database.SqlQueryRaw<AcusePersistidoFila>("""
            SELECT a."Id",a."AccesoId",a."UsuarioId",a."HuellaSolicitud",a."RegistradoEnUtc",a."Resultado",
                (SELECT count(*)::integer FROM public."AcusesDevolucionBultos" l WHERE l."AcuseId"=a."Id") AS "CantidadBultos"
            FROM public."AcusesDevolucionComercio" a WHERE a."ComercioId"=@commerceId AND a."ComandoId"=@commandId
            """, Param("commerceId", NpgsqlDbType.Uuid, commerceId), Param("commandId", NpgsqlDbType.Uuid, commandId))
            .SingleOrDefaultAsync(ct);

    private static void ExigirMismoComando(Guid accessId, Guid userId, byte[] hash, ContextoUsuario actor, byte[] expected)
    {
        if (accessId != actor.AccesoId || userId != actor.UsuarioId || hash.Length != expected.Length
            || !CryptographicOperations.FixedTimeEquals(hash, expected))
            throw new ConflictoRepartoException("El identificador del comando ya se utilizó con otro actor o contenido.");
    }

    private static byte[] HuellaTransferencia(ContextoUsuario actor, Guid shipmentId,
        TransferirDevolucionComando command, IReadOnlyList<BultoDevolucionComando> parcels)
    {
        var canonical = $"transferencia|{actor.AccesoId:N}|{actor.UsuarioId:N}|{shipmentId:N}|{command.VersionEnvio}|{command.ConfirmarTransferenciaFisica}|" +
            string.Join('|', parcels.Select(x => $"{x.BultoId:N}:{x.VersionBulto}"));
        return SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
    }

    private static byte[] HuellaAcuse(ContextoUsuario actor, Guid operatorId, Guid transferId, AcusarDevolucionComando command)
    {
        var canonical = $"acuse|{actor.AccesoId:N}|{actor.UsuarioId:N}|{operatorId:N}|{transferId:N}|{command.VersionTransferencia}|{command.Conforme}|" +
            string.Join('|', command.Bultos.OrderBy(x => x.BultoId).Select(x => $"{x.BultoId:N}:{x.VersionBulto}:{x.Observacion}"));
        return SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
    }

    private static NpgsqlParameter Param(string name, NpgsqlDbType type, object? value) => new(name, type)
    {
        Value = value ?? DBNull.Value
    };

    private static BultoDevolucionDatos ProyectarBulto(DevolucionDetalleFila x, bool puedeAcusar)
    {
        var transferred = x.TransferenciaId.HasValue;
        var acknowledged = x.AcuseId.HasValue;
        var hasReturnResult = x.Resultado is "EnDevolucion" or "Devuelto";
        var status = acknowledged ? x.ResultadoAcuse switch
        {
            "Conforme" => "Recibido conforme",
            "ConObservaciones" => "Recibido con observaciones",
            _ => "Acuse registrado"
        } : transferred ? "Pendiente de acuse" : !hasReturnResult ? "Fuera del circuito de devolución" : x.Custodia switch
        {
            "Deposito" => "En depósito — pendiente de entrega física",
            "Comercio" => "Histórico sin evidencia suficiente",
            _ => "En retorno"
        };
        var canAcknowledge = puedeAcusar && transferred && !acknowledged && x.Custodia == "Comercio";
        var blocked = canAcknowledge ? null : acknowledged ? "Ya se registró el acuse definitivo de este bulto."
            : !hasReturnResult && !transferred ? "Este bulto no tiene un resultado de devolución ni una transferencia física AP-04."
            : !puedeAcusar ? "Tu rol permite consultar, pero no registrar acuses."
            : !transferred && x.Custodia == "Comercio" ? "No hay una transferencia física AP-04 acreditada para este hecho histórico."
            : !transferred ? "El operador todavía no registró la entrega física al comercio."
            : "La custodia actual no permite acusar esta transferencia.";
        return new(x.BultoId!.Value, x.Codigo!, x.Resultado!, x.Custodia!, x.VersionBulto, x.TransferenciaId, status,
            canAcknowledge, blocked, x.ResultadoAcuse, x.Observacion, x.TransferidaEnUtc, x.AcusadoEnUtc);
    }

    private static CursorDevolucion? LeerCursor(string? cursor, ContextoUsuario actor, Guid? operatorId,
        DateTimeOffset from, DateTimeOffset to, string? situation, string? reference)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var text = cursor.Replace('-', '+').Replace('_', '/');
            text = text.PadRight((text.Length + 3) / 4 * 4, '=');
            var parsed = JsonSerializer.Deserialize<CursorDevolucion>(Convert.FromBase64String(text), Json);
            if (parsed is null || parsed.ComercioId != actor.ComercioId || parsed.OperadorId != operatorId
                || parsed.DesdeUtc != from || parsed.HastaUtc != to || parsed.Situacion != situation
                || parsed.Referencia != reference || parsed.EnvioId == Guid.Empty)
                throw new ArgumentException("El cursor no corresponde a los filtros actuales.");
            return parsed;
        }
        catch (FormatException) { throw new ArgumentException("El cursor de paginación no es válido."); }
        catch (JsonException) { throw new ArgumentException("El cursor de paginación no es válido."); }
    }

    private static string EscribirCursor(CursorDevolucion cursor)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(cursor, Json);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private sealed record CursorDevolucion(Guid ComercioId, Guid? OperadorId, DateTimeOffset DesdeUtc,
        DateTimeOffset HastaUtc, string? Situacion, string? Referencia, DateTimeOffset ActividadEnUtc, Guid EnvioId);

    private sealed class DevolucionResumenFila
    {
        public Guid EnvioId { get; set; } public Guid OperadorId { get; set; } public string OperadorNombre { get; set; } = "";
        public string Referencia { get; set; } = ""; public string EstadoEnvio { get; set; } = ""; public DateTimeOffset ActividadEnUtc { get; set; }
        public int TotalBultos { get; set; } public int EnRetorno { get; set; } public int EnDeposito { get; set; }
        public int PendientesAcuse { get; set; } public int RecibidosConObservaciones { get; set; } public string Situacion { get; set; } = "";
    }
    private sealed class DevolucionDetalleFila
    {
        public Guid EnvioId { get; set; } public Guid OperadorId { get; set; } public string OperadorNombre { get; set; } = "";
        public string Referencia { get; set; } = ""; public string EstadoEnvio { get; set; } = ""; public long VersionEnvio { get; set; }
        public bool EsRetiro { get; set; } public int TotalBultos { get; set; } public int PendientesAcuse { get; set; }
        public Guid? BultoId { get; set; } public string? Codigo { get; set; } public string? Resultado { get; set; }
        public string? Custodia { get; set; } public long VersionBulto { get; set; } public Guid? TransferenciaId { get; set; }
        public DateTimeOffset? TransferidaEnUtc { get; set; } public Guid? AcuseId { get; set; } public string? ResultadoAcuse { get; set; }
        public string? Observacion { get; set; } public DateTimeOffset? AcusadoEnUtc { get; set; }
    }
    private sealed class HechoDevolucionFila
    {
        public Guid Id { get; set; } public string Tipo { get; set; } = ""; public DateTimeOffset RegistradoEnUtc { get; set; }
        public DateTimeOffset? OcurridoEnUtc { get; set; } public string Actor { get; set; } = "";
        public int? Bultos { get; set; } public string? Resultado { get; set; } public string? Observaciones { get; set; }
    }
    private sealed record TransferenciaPersistidaFila(Guid Id, Guid AccesoId, Guid UsuarioId, byte[] HuellaSolicitud,
        DateTimeOffset RegistradaEnUtc, int CantidadBultos);
    private sealed record AcusePersistidoFila(Guid Id, Guid AccesoId, Guid UsuarioId, byte[] HuellaSolicitud,
        DateTimeOffset RegistradoEnUtc, string Resultado, int CantidadBultos);
    private sealed record TransferenciaLineaInsert(Guid BultoId, long VersionAnterior, long VersionNueva,
        string ResultadoAnterior, string CustodiaAnterior, string ResultadoNuevo, string CustodiaNueva);
    private sealed record AcuseLineaInsert(Guid BultoId, long VersionBulto, string? Observacion);
    private sealed class TransferenciaBultoFila { public Guid BultoId { get; set; } public long VersionBulto { get; set; } }
}
