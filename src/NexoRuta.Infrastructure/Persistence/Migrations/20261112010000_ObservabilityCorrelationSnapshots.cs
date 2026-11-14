using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NexoRuta.Infrastructure.Persistence.Migrations;

[DbContext(typeof(NexoRutaDbContext))]
[Migration("20261112010000_ObservabilityCorrelationSnapshots")]
public sealed class ObservabilityCorrelationSnapshots : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
-- Trace context for SQL-created operational outbox messages.
DO $$
DECLARE definition text;
BEGIN
    SELECT pg_get_functiondef('public.capture_operational_event()'::regprocedure) INTO definition;
    IF position('''correlacionId'',correlation,''ocurridaEnUtc''' in definition)=0 THEN
        RAISE EXCEPTION 'capture_operational_event source changed; update this migration explicitly';
    END IF;
    EXECUTE replace(definition,
        '''correlacionId'',correlation,''ocurridaEnUtc''',
        '''correlacionId'',correlation,''_nexoruta_traceparent'',nullif(nexoruta_app.setting(''traceparent''),''''),''ocurridaEnUtc''');
END $$;

-- Keep trace/business metadata beside each delivery, outside the signed commercial body.
ALTER TABLE public."WebhookDeliveries"
    ADD COLUMN "TraceParent" text,
    ADD COLUMN "CorrelationId" uuid;
DO $$
DECLARE definition text;
BEGIN
    SELECT pg_get_functiondef('nexoruta_app.webhook_capture()'::regprocedure) INTO definition;
    IF position('INSERT INTO public."WebhookDeliveries"("EventoId","SuscripcionId","Revision","OperadorId","ComercioId","EnvioId","Tipo","Contrato","Url","Payload","ClaveId","SecretoProtegido")' in definition)=0
        OR position('VALUES(NEW."Id",s."Id",s."Revision",e."OperadorId",commerce,shipment,CASE WHEN s."Contrato"=1 AND kind LIKE ''Retiro%'' THEN ''OperacionActualizada'' ELSE kind END,s."Contrato",s."Url",body::text,s."ClaveId",s."SecretoProtegido")' in definition)=0 THEN
        RAISE EXCEPTION 'webhook_capture source changed; update this migration explicitly';
    END IF;
    definition := replace(definition,
        '"ClaveId","SecretoProtegido")',
        '"ClaveId","SecretoProtegido","TraceParent","CorrelationId")');
    definition := replace(definition,
        's."ClaveId",s."SecretoProtegido")',
        's."ClaveId",s."SecretoProtegido",nullif(p->>''_nexoruta_traceparent'',''''),nullif(p->>''correlacionId'','''')::uuid)');
    EXECUTE definition;
END $$;

-- Authenticated API reports are account-scoped and may only update the caller's operator/access row.
CREATE TABLE public."MobileSyncSnapshots"(
    "OperadorId" uuid NOT NULL,
    "AccesoId" uuid NOT NULL,
    "InstallationId" uuid NOT NULL,
    "Sequence" bigint NOT NULL CHECK("Sequence">0),
    "PendingOperations" integer NOT NULL CHECK("PendingOperations" BETWEEN 0 AND 10000),
    "ReportedAtUtc" timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY("OperadorId","AccesoId","InstallationId")
);
CREATE INDEX mobile_sync_snapshot_age ON public."MobileSyncSnapshots"("ReportedAtUtc");
ALTER TABLE public."MobileSyncSnapshots" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."MobileSyncSnapshots" FORCE ROW LEVEL SECURITY;
GRANT SELECT,INSERT,UPDATE,DELETE ON public."MobileSyncSnapshots" TO nexoruta_api;
CREATE POLICY mobile_sync_snapshot_actor ON public."MobileSyncSnapshots" TO nexoruta_api
    USING(nexoruta_app.setting('actor_kind')='operador'
        AND "OperadorId"=nexoruta_app.ctx_uuid('org_id')
        AND "AccesoId"=nexoruta_app.ctx_uuid('access_id'))
    WITH CHECK(nexoruta_app.setting('actor_kind')='operador'
        AND "OperadorId"=nexoruta_app.ctx_uuid('org_id')
        AND "AccesoId"=nexoruta_app.ctx_uuid('access_id'));

-- The metrics reader has access only to aggregated outputs, not account-level rows.
GRANT SELECT ON public."MobileSyncSnapshots" TO nexoruta_webhook_fn;
CREATE POLICY mobile_sync_snapshot_metrics ON public."MobileSyncSnapshots"
    FOR SELECT TO nexoruta_webhook_fn USING(true);
CREATE FUNCTION nexoruta_app.observability_snapshot()
RETURNS TABLE(
    "PendingSyncOperations" bigint,
    "FreshMobileInstallations" bigint,
    "StaleMobileInstallations" bigint,
    "OldestMobileSyncReportSeconds" double precision,
    "OldestPendingWebhookSeconds" double precision,
    "PendingWebhooks" bigint,
    "RetryWebhooks" bigint,
    "DeliveredWebhooks" bigint,
    "FailedWebhooks" bigint)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path=pg_catalog AS $$
    WITH mobile AS (
        SELECT "PendingOperations", "ReportedAtUtc"
        FROM public."MobileSyncSnapshots"
    ), hooks AS (
        SELECT "Estado", "CreadaEnUtc"
        FROM public."WebhookDeliveries"
    )
    SELECT
        coalesce((SELECT sum("PendingOperations") FROM mobile WHERE "ReportedAtUtc">clock_timestamp()-interval '5 minutes'),0)::bigint,
        (SELECT count(*) FROM mobile WHERE "ReportedAtUtc">clock_timestamp()-interval '5 minutes')::bigint,
        (SELECT count(*) FROM mobile WHERE "ReportedAtUtc"<=clock_timestamp()-interval '5 minutes')::bigint,
        coalesce((SELECT extract(epoch FROM clock_timestamp()-min("ReportedAtUtc")) FROM mobile),0)::double precision,
        coalesce((SELECT extract(epoch FROM clock_timestamp()-min("CreadaEnUtc"))
            FROM hooks WHERE "Estado" IN('Pendiente','Reintento','Procesando')),0)::double precision,
        (SELECT count(*) FROM hooks WHERE "Estado"='Pendiente')::bigint,
        (SELECT count(*) FROM hooks WHERE "Estado" IN('Reintento','Procesando'))::bigint,
        (SELECT count(*) FROM hooks WHERE "Estado"='Entregado')::bigint,
        (SELECT count(*) FROM hooks WHERE "Estado"='Fallido')::bigint
$$;
ALTER FUNCTION nexoruta_app.observability_snapshot() OWNER TO nexoruta_webhook_fn;
REVOKE ALL ON FUNCTION nexoruta_app.observability_snapshot() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nexoruta_app.observability_snapshot() TO nexoruta_worker;
""");

    protected override void Down(MigrationBuilder m) => throw new NotSupportedException(
        "Observability correlation and account snapshots are retained; do not drop trace or sync history destructively.");
}
