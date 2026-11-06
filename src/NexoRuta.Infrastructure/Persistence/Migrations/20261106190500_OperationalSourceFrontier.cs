using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace NexoRuta.Infrastructure.Persistence.Migrations;

[DbContext(typeof(NexoRutaDbContext))]
[Migration("20261106190500_OperationalSourceFrontier")]
public sealed class OperationalSourceFrontier : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
CREATE TABLE public."OperacionFuente"("OperadorId" uuid PRIMARY KEY,"Capturados" bigint NOT NULL DEFAULT 0,"Proyectados" bigint NOT NULL DEFAULT 0,"Documentados" bigint NOT NULL DEFAULT 0);
ALTER TABLE public."OperacionFuente" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."OperacionFuente" FORCE ROW LEVEL SECURITY;
GRANT SELECT,INSERT,UPDATE ON public."OperacionFuente" TO nexoruta_history_fn,nexoruta_job_fn;
CREATE POLICY operational_frontier_capture ON public."OperacionFuente" TO nexoruta_history_fn USING(true) WITH CHECK(true);
CREATE POLICY operational_frontier_consume ON public."OperacionFuente" TO nexoruta_job_fn USING(true) WITH CHECK(true);
GRANT SELECT ON public."OperacionFuente" TO nexoruta_api;
CREATE POLICY operational_frontier_operator ON public."OperacionFuente" FOR SELECT TO nexoruta_api USING(
 nexoruta_app.setting('actor_kind')='operador' AND "OperadorId"=nexoruta_app.ctx_uuid('org_id') OR
 nexoruta_app.setting('actor_kind')='comercio' AND "OperadorId"=nexoruta_app.ctx_uuid('selected_operator_id'));
CREATE FUNCTION public.operational_capture_frontier() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 INSERT INTO public."OperacionFuente"("OperadorId","Capturados") VALUES(NEW."OperadorId",1)
 ON CONFLICT("OperadorId") DO UPDATE SET "Capturados"="OperacionFuente"."Capturados"+1;
 RETURN NEW;
END $$;
ALTER FUNCTION public.operational_capture_frontier() OWNER TO nexoruta_history_fn;
REVOKE ALL ON FUNCTION public.operational_capture_frontier() FROM PUBLIC;
CREATE TRIGGER operational_frontier AFTER INSERT ON public."OutboxMessages" FOR EACH ROW WHEN(NEW."Tipo"='EventoOperativo') EXECUTE FUNCTION public.operational_capture_frontier();
CREATE FUNCTION public.operational_applied_frontier() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 INSERT INTO public."OperacionFuente"("OperadorId","Proyectados") VALUES(NEW."OperadorId",1)
 ON CONFLICT("OperadorId") DO UPDATE SET "Proyectados"="OperacionFuente"."Proyectados"+1;
 RETURN NEW;
END $$;
ALTER FUNCTION public.operational_applied_frontier() OWNER TO nexoruta_job_fn;
REVOKE ALL ON FUNCTION public.operational_applied_frontier() FROM PUBLIC;
CREATE TRIGGER operational_frontier AFTER INSERT ON public."OperacionEventosProcesados" FOR EACH ROW EXECUTE FUNCTION public.operational_applied_frontier();
CREATE TABLE public."OperacionDocumentosProcesados"("EventoId" uuid PRIMARY KEY,"OperadorId" uuid NOT NULL);
ALTER TABLE public."OperacionDocumentosProcesados" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."OperacionDocumentosProcesados" FORCE ROW LEVEL SECURITY;
GRANT INSERT,SELECT ON public."OperacionDocumentosProcesados" TO nexoruta_job_fn;
CREATE POLICY operational_document_ack ON public."OperacionDocumentosProcesados" TO nexoruta_job_fn USING(true) WITH CHECK(true);
CREATE FUNCTION nexoruta_app.ack_operational_document(p_id uuid,p_operator uuid) RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE changed int;
BEGIN
 IF p_id IS NULL OR p_operator IS NULL THEN RAISE EXCEPTION 'Invalid documentary acknowledgement'; END IF;
 INSERT INTO public."OperacionDocumentosProcesados" VALUES(p_id,p_operator) ON CONFLICT DO NOTHING;
 GET DIAGNOSTICS changed=ROW_COUNT;
 IF changed=1 THEN
  INSERT INTO public."OperacionFuente"("OperadorId","Documentados") VALUES(p_operator,1)
  ON CONFLICT("OperadorId") DO UPDATE SET "Documentados"="OperacionFuente"."Documentados"+1;
 END IF;
END $$;
ALTER FUNCTION nexoruta_app.ack_operational_document(uuid,uuid) OWNER TO nexoruta_job_fn;
REVOKE ALL ON FUNCTION nexoruta_app.ack_operational_document(uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nexoruta_app.ack_operational_document(uuid,uuid) TO nexoruta_worker;
CREATE FUNCTION nexoruta_app.operational_projection_status(p_projection text,p_operator uuid) RETURNS text
 LANGUAGE sql STABLE SECURITY INVOKER SET search_path=pg_catalog AS $$
 SELECT CASE WHEN count(*)=0 OR min("VerificadaEnUtc")<statement_timestamp()-interval '15 seconds' THEN 'Indisponible'
 WHEN count(*)<>max("Particiones") OR max("Atraso")>0 OR EXISTS(SELECT 1 FROM public."OperacionFuente" f WHERE f."OperadorId"=p_operator AND f."Capturados"<>CASE WHEN p_projection='board' THEN f."Proyectados" ELSE f."Documentados" END) THEN 'Atrasada'
 ELSE 'Actualizada' END FROM public."OperacionProyeccionSalud" WHERE "Proyeccion"=p_projection
 $$;
ALTER FUNCTION nexoruta_app.operational_projection_status(text,uuid) OWNER TO nexoruta_job_fn;
REVOKE ALL ON FUNCTION nexoruta_app.operational_projection_status(text,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nexoruta_app.operational_projection_status(text,uuid) TO nexoruta_api,nexoruta_public_fn;
ALTER TABLE public."OperacionFuente" OWNER TO nexoruta_schema_owner;
ALTER TABLE public."OperacionDocumentosProcesados" OWNER TO nexoruta_schema_owner;
""");
    protected override void Down(MigrationBuilder m) => throw new NotSupportedException();
}
