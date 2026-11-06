using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace NexoRuta.Infrastructure.Persistence.Migrations;

[DbContext(typeof(NexoRutaDbContext))]
[Migration("20261106190100_OperationalProjection")]
public sealed class OperationalProjection : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
CREATE TABLE public."OperacionEventosProcesados"("EventoId" uuid PRIMARY KEY,"OperadorId" uuid NOT NULL,"Payload" jsonb NOT NULL);
CREATE TABLE public."OperacionProyeccion"("OperadorId" uuid NOT NULL,"Tipo" text NOT NULL,"EntidadId" uuid NOT NULL,"ComercioId" uuid,"EnvioId" uuid,"RutaId" uuid,"Version" bigint NOT NULL,"EventoId" uuid NOT NULL,"OcurridaEnUtc" timestamptz,"RegistradaEnUtc" timestamptz NOT NULL,"Evento" jsonb NOT NULL,"Revision" bigint GENERATED ALWAYS AS IDENTITY,PRIMARY KEY("OperadorId","Tipo","EntidadId"));
CREATE INDEX ON public."OperacionProyeccion"("OperadorId","RutaId");
CREATE INDEX ON public."OperacionProyeccion"("OperadorId","Revision");
ALTER TABLE public."OperacionEventosProcesados" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."OperacionEventosProcesados" FORCE ROW LEVEL SECURITY;
ALTER TABLE public."OperacionProyeccion" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."OperacionProyeccion" FORCE ROW LEVEL SECURITY;
GRANT SELECT,INSERT ON public."OperacionEventosProcesados" TO nexoruta_job_fn;
GRANT SELECT,INSERT,UPDATE ON public."OperacionProyeccion" TO nexoruta_job_fn;
GRANT USAGE ON SEQUENCE public."OperacionProyeccion_Revision_seq" TO nexoruta_job_fn;
CREATE POLICY operational_dedup_worker ON public."OperacionEventosProcesados" TO nexoruta_job_fn USING(true) WITH CHECK(true);
CREATE POLICY operational_projection_worker ON public."OperacionProyeccion" TO nexoruta_job_fn USING(true) WITH CHECK(true);
GRANT SELECT ON public."OperacionProyeccion" TO nexoruta_api;
CREATE POLICY operational_projection_tenant ON public."OperacionProyeccion" FOR SELECT TO nexoruta_api USING(
 nexoruta_app.setting('actor_kind')='operador' AND "OperadorId"=nexoruta_app.ctx_uuid('org_id') OR
 nexoruta_app.setting('actor_kind')='comercio' AND "ComercioId"=nexoruta_app.ctx_uuid('org_id') AND "OperadorId"=nexoruta_app.ctx_uuid('selected_operator_id'));
CREATE FUNCTION nexoruta_app.project_operational_event(p_event jsonb) RETURNS boolean
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE event_id uuid:=(p_event->>'eventId')::uuid; operator_id uuid:=(p_event->>'operadorId')::uuid;
 existing jsonb; changed int;
BEGIN
 IF p_event->>'schemaVersion'<>'1' OR event_id IS NULL OR operator_id IS NULL
 OR (p_event->>'entidadId')::uuid IS NULL OR (p_event->>'version')::bigint<0
 OR p_event->>'tipo' NOT IN ('Envio','Bulto','Posicion','Ruta','Parada','Estimacion','Flota') THEN RAISE EXCEPTION 'Invalid operational envelope'; END IF;
 INSERT INTO public."OperacionEventosProcesados" VALUES(event_id,operator_id,p_event) ON CONFLICT DO NOTHING;
 GET DIAGNOSTICS changed=ROW_COUNT;
 IF changed=0 THEN
  SELECT "Payload" INTO existing FROM public."OperacionEventosProcesados" WHERE "EventoId"=event_id;
  IF existing<>p_event THEN RAISE EXCEPTION 'Event identity reused with different content'; END IF;
  RETURN false;
 END IF;
 -- Retain inadmissible future GPS in history/dedup without making it the current position.
 IF p_event->>'tipo'='Posicion' AND (p_event->>'ocurridaEnUtc')::timestamptz>(p_event->>'registradaEnUtc')::timestamptz THEN RETURN true; END IF;
 INSERT INTO public."OperacionProyeccion" AS current("OperadorId","Tipo","EntidadId","ComercioId","EnvioId","RutaId","Version","EventoId","OcurridaEnUtc","RegistradaEnUtc","Evento")
 VALUES(operator_id,p_event->>'tipo',(p_event->>'entidadId')::uuid,(p_event->>'comercioId')::uuid,(p_event->>'envioId')::uuid,(p_event->>'rutaId')::uuid,(p_event->>'version')::bigint,event_id,(p_event->>'ocurridaEnUtc')::timestamptz,(p_event->>'registradaEnUtc')::timestamptz,p_event)
 ON CONFLICT("OperadorId","Tipo","EntidadId") DO UPDATE SET
 "ComercioId"=excluded."ComercioId","EnvioId"=excluded."EnvioId","RutaId"=excluded."RutaId","Version"=excluded."Version","EventoId"=excluded."EventoId","OcurridaEnUtc"=excluded."OcurridaEnUtc","RegistradaEnUtc"=excluded."RegistradaEnUtc","Evento"=excluded."Evento","Revision"=DEFAULT
 WHERE CASE WHEN excluded."Tipo"='Posicion' THEN
  (excluded."OcurridaEnUtc",-coalesce((excluded."Evento"->'datos'->>'precisionMetros')::numeric,1e10),excluded."EventoId") >
  (current."OcurridaEnUtc",-coalesce((current."Evento"->'datos'->>'precisionMetros')::numeric,1e10),current."EventoId")
 ELSE (excluded."Version",excluded."RegistradaEnUtc",excluded."EventoId") > (current."Version",current."RegistradaEnUtc",current."EventoId") END;
 RETURN true;
END $$;
ALTER FUNCTION nexoruta_app.project_operational_event(jsonb) OWNER TO nexoruta_job_fn;
REVOKE ALL ON FUNCTION nexoruta_app.project_operational_event(jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nexoruta_app.project_operational_event(jsonb) TO nexoruta_worker;
ALTER TABLE public."OperacionEventosProcesados" OWNER TO nexoruta_schema_owner;
ALTER TABLE public."OperacionProyeccion" OWNER TO nexoruta_schema_owner;
""");
    protected override void Down(MigrationBuilder m) => throw new NotSupportedException("Rebuild projections in an isolated destination instead of destructive rollback.");
}
