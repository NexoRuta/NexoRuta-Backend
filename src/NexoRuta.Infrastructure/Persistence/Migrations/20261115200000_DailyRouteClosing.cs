using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace NexoRuta.Infrastructure.Persistence.Migrations;

[DbContext(typeof(NexoRutaDbContext))]
[Migration("20261115200000_DailyRouteClosing")]
public sealed class DailyRouteClosing : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
DO $$ BEGIN
 IF NOT EXISTS(SELECT FROM pg_roles WHERE rolname='nexoruta_closing_fn') THEN
 CREATE ROLE nexoruta_closing_fn NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
 END IF;
END $$;
GRANT USAGE ON SCHEMA public,nexoruta_app TO nexoruta_closing_fn;
GRANT EXECUTE ON FUNCTION nexoruta_app.setting(text),nexoruta_app.ctx_uuid(text) TO nexoruta_closing_fn;
CREATE TABLE public."CierresDiarios"(
 "Id" uuid PRIMARY KEY,"RutaId" uuid NOT NULL UNIQUE,"OperadorId" uuid NOT NULL,"RepartidorAccesoId" uuid NOT NULL,
 "WorkerId" uuid NOT NULL,"FechaOperativa" date NOT NULL,"CorteEnUtc" timestamptz NOT NULL,
 "IniciadoEnUtc" timestamptz NOT NULL,"FinalizadoEnUtc" timestamptz,"TransaccionId" bigint NOT NULL,
 "Resultado" text NOT NULL CHECK("Resultado" IN('Procesando','Aplicada')),
 "IncidenciasCierre" jsonb NOT NULL DEFAULT '[]',"Detalle" text,
 FOREIGN KEY("OperadorId","RutaId") REFERENCES public."HojasRuta"("OperadorId","Id"));
ALTER TABLE public."CierresDiarios" OWNER TO nexoruta_schema_owner;
ALTER TABLE public."CierresDiarios" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."CierresDiarios" FORCE ROW LEVEL SECURITY;
CREATE POLICY closing_fn_audit ON public."CierresDiarios" TO nexoruta_closing_fn USING(true) WITH CHECK(true);
CREATE POLICY closing_worker_audit ON public."CierresDiarios" FOR SELECT TO nexoruta_worker
 USING("TransaccionId"=txid_current());
CREATE POLICY closing_api_audit ON public."CierresDiarios" FOR SELECT TO nexoruta_api
 USING(nexoruta_app.setting('actor_kind')='operador' AND "OperadorId"=nexoruta_app.ctx_uuid('org_id'));
GRANT SELECT,INSERT,UPDATE ON public."CierresDiarios" TO nexoruta_closing_fn;
GRANT SELECT ON public."CierresDiarios" TO nexoruta_worker,nexoruta_api;
CREATE FUNCTION public.guard_daily_closing() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF TG_OP='UPDATE' AND OLD."Resultado"='Procesando' AND NEW."Resultado"='Aplicada'
 AND OLD."TransaccionId"=txid_current() AND current_user='nexoruta_closing_fn'
 AND (to_jsonb(OLD)-ARRAY['Resultado','FinalizadoEnUtc','IncidenciasCierre','Detalle'])=(to_jsonb(NEW)-ARRAY['Resultado','FinalizadoEnUtc','IncidenciasCierre','Detalle']) THEN RETURN NEW; END IF;
 RAISE EXCEPTION 'Daily closing history is immutable' USING ERRCODE='55000';
END $$;
CREATE TRIGGER closing_immutable BEFORE UPDATE OR DELETE ON public."CierresDiarios" FOR EACH ROW EXECUTE FUNCTION public.guard_daily_closing();
CREATE TRIGGER closing_no_truncate BEFORE TRUNCATE ON public."CierresDiarios" FOR EACH STATEMENT EXECUTE FUNCTION public.guard_daily_closing();

GRANT SELECT ON public."HojasRuta",public."ConfiguracionesReparto",public."Operadores" TO nexoruta_closing_fn;
GRANT UPDATE("Id") ON public."Operadores" TO nexoruta_closing_fn;
CREATE POLICY closing_fn_routes ON public."HojasRuta" FOR SELECT TO nexoruta_closing_fn USING(true);
CREATE POLICY closing_fn_config ON public."ConfiguracionesReparto" FOR SELECT TO nexoruta_closing_fn USING(true);
CREATE POLICY closing_fn_operators ON public."Operadores" TO nexoruta_closing_fn USING(true) WITH CHECK(false);
GRANT CREATE ON SCHEMA nexoruta_app TO nexoruta_closing_fn;
CREATE FUNCTION nexoruta_app.claim_daily_closing(p_worker uuid,p_now timestamptz,p_cutoff time)
 RETURNS TABLE("Id" uuid,"RutaId" uuid,"OperadorId" uuid,"CorteEnUtc" timestamptz)
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE candidate record; operation uuid;
BEGIN
 IF p_worker IS NULL OR p_worker='00000000-0000-0000-0000-000000000000'::uuid OR p_now IS NULL OR p_cutoff IS NULL THEN RAISE EXCEPTION 'Invalid daily closing claim'; END IF;
 FOR candidate IN SELECT h."Id",h."OperadorId",h."RepartidorAccesoId",h."Fecha",((h."Fecha"+1)+p_cutoff) AT TIME ZONE c."ZonaHoraria" AS cutoff
 FROM public."HojasRuta" h JOIN public."ConfiguracionesReparto" c ON c."OperadorId"=h."OperadorId" AND c."Version"=h."ConfiguracionVersion"
 WHERE h."Estado"='EnCurso' AND ((h."Fecha"+1)+p_cutoff) AT TIME ZONE c."ZonaHoraria"<=p_now
 AND NOT EXISTS(SELECT 1 FROM public."CierresDiarios" d WHERE d."RutaId"=h."Id") ORDER BY h."Fecha",h."Id"
 LOOP
 -- Same lock order as mobile execution and Backoffice: operator first, then route.
 PERFORM 1 FROM public."Operadores" o WHERE o."Id"=candidate."OperadorId" FOR UPDATE SKIP LOCKED;
 IF NOT FOUND THEN CONTINUE; END IF;
 IF NOT EXISTS(SELECT 1 FROM public."HojasRuta" h WHERE h."Id"=candidate."Id" AND h."Estado"='EnCurso')
 OR EXISTS(SELECT 1 FROM public."CierresDiarios" d WHERE d."RutaId"=candidate."Id") THEN CONTINUE; END IF;
 operation:=gen_random_uuid();
 INSERT INTO public."CierresDiarios"("Id","RutaId","OperadorId","RepartidorAccesoId","WorkerId","FechaOperativa","CorteEnUtc","IniciadoEnUtc","TransaccionId","Resultado")
 VALUES(operation,candidate."Id",candidate."OperadorId",candidate."RepartidorAccesoId",p_worker,candidate."Fecha",candidate.cutoff,p_now,txid_current(),'Procesando');
 RETURN QUERY SELECT operation,candidate."Id",candidate."OperadorId",candidate.cutoff;
 RETURN;
 END LOOP;
END $$;
ALTER FUNCTION nexoruta_app.claim_daily_closing(uuid,timestamptz,time) OWNER TO nexoruta_closing_fn;
REVOKE ALL ON FUNCTION nexoruta_app.claim_daily_closing(uuid,timestamptz,time) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nexoruta_app.claim_daily_closing(uuid,timestamptz,time) TO nexoruta_worker;
CREATE FUNCTION nexoruta_app.finish_daily_closing(p_id uuid,p_now timestamptz,p_incidents jsonb,p_detail text) RETURNS boolean
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 UPDATE public."CierresDiarios" SET "Resultado"='Aplicada',"FinalizadoEnUtc"=p_now,"IncidenciasCierre"=p_incidents,"Detalle"=p_detail
 WHERE "Id"=p_id AND "Resultado"='Procesando' AND "TransaccionId"=txid_current();
 RETURN FOUND;
END $$;
ALTER FUNCTION nexoruta_app.finish_daily_closing(uuid,timestamptz,jsonb,text) OWNER TO nexoruta_closing_fn;
REVOKE ALL ON FUNCTION nexoruta_app.finish_daily_closing(uuid,timestamptz,jsonb,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nexoruta_app.finish_daily_closing(uuid,timestamptz,jsonb,text) TO nexoruta_worker;
CREATE FUNCTION nexoruta_app.check_daily_closing_completed() RETURNS trigger
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF EXISTS(SELECT 1 FROM public."CierresDiarios" d JOIN public."HojasRuta" h ON h."Id"=d."RutaId"
 WHERE d."Id"=NEW."Id" AND (d."Resultado"<>'Aplicada' OR h."Estado"<>'Finalizada' OR h."ReservaRecursosActiva")) THEN
 RAISE EXCEPTION 'Daily closing must complete atomically' USING ERRCODE='23514'; END IF;
 RETURN NULL;
END $$;
ALTER FUNCTION nexoruta_app.check_daily_closing_completed() OWNER TO nexoruta_closing_fn;
REVOKE ALL ON FUNCTION nexoruta_app.check_daily_closing_completed() FROM PUBLIC;
CREATE CONSTRAINT TRIGGER closing_completed AFTER INSERT OR UPDATE ON public."CierresDiarios"
 DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION nexoruta_app.check_daily_closing_completed();
REVOKE CREATE ON SCHEMA nexoruta_app FROM nexoruta_closing_fn;

GRANT SELECT ON public."HojasRuta",public."ParadasRuta",public."Bultos",public."Envios" TO nexoruta_worker;
GRANT UPDATE("Estado","Version","ReservaRecursosActiva","FinEnUtc") ON public."HojasRuta" TO nexoruta_worker;
GRANT UPDATE("Activa","ResultadoVisita","MotivoSuspension") ON public."ParadasRuta" TO nexoruta_worker;
CREATE POLICY closing_worker_routes_read ON public."HojasRuta" FOR SELECT TO nexoruta_worker
 USING(EXISTS(SELECT 1 FROM public."CierresDiarios" d WHERE d."RutaId"="HojasRuta"."Id" AND d."TransaccionId"=txid_current() AND d."Resultado"='Procesando'));
CREATE POLICY closing_worker_routes_update ON public."HojasRuta" FOR UPDATE TO nexoruta_worker
 USING(EXISTS(SELECT 1 FROM public."CierresDiarios" d WHERE d."RutaId"="HojasRuta"."Id" AND d."TransaccionId"=txid_current() AND d."Resultado"='Procesando'))
 WITH CHECK("Estado"='Finalizada' AND NOT "ReservaRecursosActiva");
CREATE POLICY closing_worker_stops ON public."ParadasRuta" TO nexoruta_worker
 USING(EXISTS(SELECT 1 FROM public."CierresDiarios" d WHERE d."RutaId"="ParadasRuta"."RutaId" AND d."TransaccionId"=txid_current() AND d."Resultado"='Procesando'))
 WITH CHECK(EXISTS(SELECT 1 FROM public."CierresDiarios" d WHERE d."RutaId"="ParadasRuta"."RutaId" AND d."TransaccionId"=txid_current() AND d."Resultado"='Procesando'));
CREATE POLICY closing_worker_shipments ON public."Envios" FOR SELECT TO nexoruta_worker
 USING(EXISTS(SELECT 1 FROM public."ParadasRuta" p WHERE p."EnvioId"="Envios"."Id"));
CREATE POLICY closing_worker_parcels ON public."Bultos" FOR SELECT TO nexoruta_worker
 USING(EXISTS(SELECT 1 FROM public."ParadasRuta" p WHERE p."EnvioId"="Bultos"."EnvioId"));
""");
    protected override void Down(MigrationBuilder m) => throw new NotSupportedException("Daily closing history must be retained.");
}
