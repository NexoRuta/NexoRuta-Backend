using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace NexoRuta.Infrastructure.Persistence.Migrations;
[DbContext(typeof(NexoRutaDbContext))]
[Migration("20261108010000_ManagementReporting")]
public sealed class ManagementReporting : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
CREATE TABLE public."GestionPendientes"("OperadorId" uuid NOT NULL,"EnvioId" uuid NOT NULL,"Generacion" bigint NOT NULL DEFAULT 1,"Aplicada" bigint NOT NULL DEFAULT 0,"ActualizadaEnUtc" timestamptz,PRIMARY KEY("OperadorId","EnvioId"));
CREATE TABLE public."GestionOperaciones"("OperadorId" uuid NOT NULL,"EnvioId" uuid NOT NULL,"ComercioId" uuid NOT NULL,"ZonaId" uuid,"Zona" text NOT NULL,"EsRetiro" boolean NOT NULL,"Estado" text NOT NULL,"AdmitidaEnUtc" timestamptz NOT NULL,"CompromisoDesdeUtc" timestamptz,"CompromisoHastaUtc" timestamptz,"CompletadaEnUtc" timestamptz,"EnFranja" boolean,"PruebaPendiente" boolean NOT NULL,"Bultos" int NOT NULL,"PesoKg" numeric NOT NULL,"VolumenM3" numeric NOT NULL,"Entregados" int NOT NULL,"Pendientes" int NOT NULL,"Devueltos" int NOT NULL,PRIMARY KEY("OperadorId","EnvioId"));
CREATE TABLE public."GestionBultos"("OperadorId" uuid NOT NULL,"EnvioId" uuid NOT NULL,"BultoId" uuid NOT NULL,"RepartidorId" uuid,"RutaId" uuid,"EntregadoEnUtc" timestamptz,"Resultado" text NOT NULL,"EnFranja" boolean,PRIMARY KEY("OperadorId","BultoId"));
CREATE TABLE public."GestionMotivos"("OperadorId" uuid NOT NULL,"EnvioId" uuid NOT NULL,"OperacionId" uuid NOT NULL,"RepartidorId" uuid NOT NULL,"RutaId" uuid NOT NULL,"OcurridaEnUtc" timestamptz NOT NULL,"Tipo" text NOT NULL,"Motivo" text NOT NULL,"Bultos" int NOT NULL,PRIMARY KEY("OperadorId","OperacionId","Tipo","Motivo"));
CREATE INDEX ON public."GestionOperaciones"("OperadorId","AdmitidaEnUtc","EnvioId");
CREATE INDEX ON public."GestionOperaciones"("OperadorId","ComercioId","ZonaId","AdmitidaEnUtc");
CREATE INDEX ON public."GestionBultos"("OperadorId","RepartidorId","EnvioId");
CREATE INDEX ON public."GestionMotivos"("OperadorId","OcurridaEnUtc","EnvioId");
CREATE INDEX ON public."GestionPendientes"("OperadorId","EnvioId") WHERE "Generacion"<>"Aplicada";
DO $m$ DECLARE t text; BEGIN
 FOREACH t IN ARRAY ARRAY['GestionPendientes','GestionOperaciones','GestionBultos','GestionMotivos'] LOOP
  EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY',t);
  EXECUTE format('ALTER TABLE public.%I FORCE ROW LEVEL SECURITY',t);
  EXECUTE format('GRANT SELECT,INSERT,UPDATE,DELETE ON public.%I TO nexoruta_job_fn',t);
  EXECUTE format('CREATE POLICY management_worker ON public.%I TO nexoruta_job_fn USING(true) WITH CHECK(true)',t);
  EXECUTE format('GRANT SELECT ON public.%I TO nexoruta_api',t);
  EXECUTE format('CREATE POLICY management_operator ON public.%I FOR SELECT TO nexoruta_api USING(nexoruta_app.setting(''actor_kind'')=''operador'' AND "OperadorId"=nexoruta_app.ctx_uuid(''org_id''))',t);
 END LOOP;
END $m$;
GRANT SELECT,INSERT,UPDATE ON public."GestionPendientes" TO nexoruta_history_fn;
CREATE POLICY management_capture ON public."GestionPendientes" TO nexoruta_history_fn USING(true) WITH CHECK(true);
-- Additive source notifications, in the same transaction as each accepted fact.
CREATE FUNCTION public.mark_management_dirty() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE j jsonb:=to_jsonb(NEW); shipment uuid;
BEGIN
 shipment:=CASE WHEN TG_TABLE_NAME='Envios' THEN (j->>'Id')::uuid ELSE (j->>'EnvioId')::uuid END;
 IF shipment IS NOT NULL THEN
 INSERT INTO public."GestionPendientes"("OperadorId","EnvioId") VALUES((j->>'OperadorId')::uuid,shipment)
 ON CONFLICT("OperadorId","EnvioId") DO UPDATE SET "Generacion"="GestionPendientes"."Generacion"+1;
 END IF;
 RETURN NEW;
END $$;
ALTER FUNCTION public.mark_management_dirty() OWNER TO nexoruta_history_fn;
REVOKE ALL ON FUNCTION public.mark_management_dirty() FROM PUBLIC;
CREATE TRIGGER management_dirty AFTER INSERT OR UPDATE ON public."Envios" FOR EACH ROW EXECUTE FUNCTION public.mark_management_dirty();
CREATE TRIGGER management_dirty AFTER INSERT OR UPDATE ON public."Bultos" FOR EACH ROW EXECUTE FUNCTION public.mark_management_dirty();
CREATE TRIGGER management_dirty AFTER INSERT ON public."TarifasEnvio" FOR EACH ROW EXECUTE FUNCTION public.mark_management_dirty();
CREATE TRIGGER management_dirty AFTER INSERT ON public."OperacionesJornada" FOR EACH ROW EXECUTE FUNCTION public.mark_management_dirty();
-- Restricted worker may read sources only inside the following definer function.
DO $m$ DECLARE t text; BEGIN
 FOREACH t IN ARRAY ARRAY['Envios','Bultos','OperacionesJornada','TarifasEnvio','VersionesReglasOperativas','ZonasCoberturaRevisiones'] LOOP
 EXECUTE format('GRANT SELECT ON public.%I TO nexoruta_job_fn',t);
 EXECUTE format('CREATE POLICY management_source ON public.%I FOR SELECT TO nexoruta_job_fn USING(true)',t);
 END LOOP;
END $m$;
CREATE FUNCTION nexoruta_app.management_in_slot(p_time timestamptz,p_snapshot jsonb) RETURNS boolean LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT CASE WHEN p_time IS NULL OR p_snapshot IS NULL THEN NULL ELSE EXISTS(
 SELECT 1 FROM jsonb_array_elements(p_snapshot->'franjas') f
 CROSS JOIN LATERAL(SELECT p_time AT TIME ZONE (p_snapshot->>'zonaHoraria') AS instant) local
 CROSS JOIN (VALUES(0),(1)) days(previous)
 WHERE f->>'estado'='Activa' AND (f->>'diaSemanaIso')::int=extract(isodow FROM local.instant::date-days.previous)
 AND local.instant >= ((local.instant::date-days.previous)+(f->>'horaInicio')::time)
 AND local.instant < ((local.instant::date-days.previous)+(f->>'horaFin')::time+CASE WHEN (f->>'horaFin')::time<(f->>'horaInicio')::time THEN interval '1 day' ELSE interval '0 days' END)) END
$$;
ALTER FUNCTION nexoruta_app.management_in_slot(timestamptz,jsonb) OWNER TO nexoruta_job_fn;
REVOKE ALL ON FUNCTION nexoruta_app.management_in_slot(timestamptz,jsonb) FROM PUBLIC;
CREATE FUNCTION nexoruta_app.reconcile_management(p_operator uuid DEFAULT NULL,p_shipment uuid DEFAULT NULL,p_limit int DEFAULT 100) RETURNS int
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE q record; n int:=0; coverage jsonb; deadline timestamptz; start_at timestamptz; e public."Envios";
BEGIN
 IF p_limit NOT BETWEEN 1 AND 1000 THEN RAISE EXCEPTION 'Invalid batch size'; END IF;
 FOR q IN SELECT * FROM public."GestionPendientes" WHERE "Generacion"<>"Aplicada"
 AND (p_operator IS NULL OR "OperadorId"=p_operator) AND (p_shipment IS NULL OR "EnvioId"=p_shipment)
 ORDER BY "OperadorId","EnvioId" LIMIT p_limit FOR UPDATE SKIP LOCKED LOOP
 SELECT * INTO STRICT e FROM public."Envios" WHERE "OperadorId"=q."OperadorId" AND "Id"=q."EnvioId";
 SELECT z."SnapshotJson",CASE WHEN e."EsRetiro" THEN e."FranjaRetiroHastaUtc" ELSE e."CreadoEnUtc"+make_interval(mins=>CASE t."Servicio"->>'Modalidad' WHEN 'Estandar' THEN r."PlazoEstandarMinutos" WHEN 'Urgente' THEN r."PlazoUrgenteMinutos" END) END
 INTO coverage,deadline FROM public."VersionesReglasOperativas" r
 LEFT JOIN LATERAL(SELECT * FROM public."TarifasEnvio" WHERE "OperadorId"=e."OperadorId" AND "EnvioId"=e."Id" ORDER BY "CreadoEnUtc","Id" LIMIT 1)t ON true
 LEFT JOIN public."ZonasCoberturaRevisiones" z ON z."OperadorId"=t."OperadorId" AND z."ZonaId"=(t."Cobertura"->>'ZonaId')::uuid AND z."NumeroRevision"=(t."Cobertura"->>'RevisionZona')::int
 WHERE r."OperadorId"=e."OperadorId" AND r."Id"=e."VersionReglasId";
 -- A delivery requires the captured coverage schedule too; no planned arrival is consulted.
 IF NOT e."EsRetiro" AND (coverage IS NULL OR NOT EXISTS(SELECT 1 FROM jsonb_array_elements(coverage->'franjas') f WHERE f->>'estado'='Activa')) THEN deadline:=NULL; END IF;
 DELETE FROM public."GestionBultos" WHERE "OperadorId"=e."OperadorId" AND "EnvioId"=e."Id";
 INSERT INTO public."GestionBultos"
 SELECT b."OperadorId",b."EnvioId",b."Id",o."AccesoId",o."RutaId",CASE WHEN e."EsRetiro" THEN (o."Payload"->>'ocurridaEn')::timestamptz ELSE b."EntregadoEnUtc" END,CASE WHEN e."EsRetiro" THEN CASE b."Custodia" WHEN 'Comercio' THEN 'EntregadoComercio' WHEN 'Deposito' THEN 'RetiroEnDeposito' WHEN 'Repartidor' THEN 'Retirado' WHEN 'PendienteDeposito' THEN 'RetiroPendienteDeposito' ELSE 'RetiroPendiente' END ELSE b."Resultado" END,
 CASE WHEN e."EsRetiro" THEN (o."Payload"->>'ocurridaEn')::timestamptz>=e."FranjaRetiroDesdeUtc" ELSE nexoruta_app.management_in_slot(b."EntregadoEnUtc",coverage) END
 FROM public."Bultos" b LEFT JOIN LATERAL(
 SELECT j.* FROM public."OperacionesJornada" j WHERE j."OperadorId"=b."OperadorId" AND j."EnvioId"=b."EnvioId" AND j."Resultado"='Aplicada'
 AND ((NOT e."EsRetiro" AND j."Tipo" IN ('EntregaFisica','EntregaBultos') AND (j."Payload"->>'ocurridaEn')::timestamptz=b."EntregadoEnUtc"
 AND (j."Tipo"='EntregaFisica' OR EXISTS(SELECT 1 FROM jsonb_array_elements(j."Payload"->'datos'->'resultadosBultos') r WHERE (r->>'bultoId')::uuid=b."Id" AND (r->>'entregado')::boolean)))
 OR (e."EsRetiro" AND j."Tipo"='RetirarBultos')) ORDER BY j."RecibidaEnUtc",j."Id" LIMIT 1)o ON true
 WHERE b."OperadorId"=e."OperadorId" AND b."EnvioId"=e."Id";
 INSERT INTO public."GestionOperaciones"
 SELECT e."OperadorId",e."Id",e."ComercioId",(t."Cobertura"->>'ZonaId')::uuid,coalesce(coverage->>'nombre',t."Cobertura"->>'ZonaId','Sin zona capturada'),e."EsRetiro",e."Estado",e."CreadoEnUtc",e."FranjaRetiroDesdeUtc",deadline,
 CASE WHEN e."EsRetiro" AND count(*)=count(g."EntregadoEnUtc") THEN max(g."EntregadoEnUtc") WHEN NOT e."EsRetiro" AND count(*)=count(b."EntregadoEnUtc") THEN max(b."EntregadoEnUtc") END,
 bool_and(g."EnFranja"),bool_or(b."Resultado" IN ('EntregaPendienteValidacion','EntregaSinValidacion')),count(*)::int,sum(b."PesoGramos")/1000,sum(b."LargoCentimetros"*b."AnchoCentimetros"*b."AltoCentimetros")/1000000,
 count(*) FILTER(WHERE CASE WHEN e."EsRetiro" THEN b."Custodia"='Comercio' ELSE b."Resultado"='Entregado' END)::int,
 count(*) FILTER(WHERE CASE WHEN e."EsRetiro" THEN b."Custodia"<>'Comercio' ELSE b."Resultado"='Pendiente' END)::int,count(*) FILTER(WHERE b."Resultado" IN ('EnDevolucion','Devuelto'))::int
 FROM public."Bultos" b JOIN public."GestionBultos" g ON g."OperadorId"=b."OperadorId" AND g."BultoId"=b."Id"
 LEFT JOIN LATERAL(SELECT * FROM public."TarifasEnvio" WHERE "OperadorId"=e."OperadorId" AND "EnvioId"=e."Id" ORDER BY "CreadoEnUtc","Id" LIMIT 1)t ON true
 WHERE b."OperadorId"=e."OperadorId" AND b."EnvioId"=e."Id" GROUP BY t."Cobertura"
 ON CONFLICT("OperadorId","EnvioId") DO UPDATE SET "ZonaId"=excluded."ZonaId","Zona"=excluded."Zona","Estado"=excluded."Estado","CompromisoDesdeUtc"=excluded."CompromisoDesdeUtc","CompromisoHastaUtc"=excluded."CompromisoHastaUtc","CompletadaEnUtc"=excluded."CompletadaEnUtc","EnFranja"=excluded."EnFranja","PruebaPendiente"=excluded."PruebaPendiente","Bultos"=excluded."Bultos","PesoKg"=excluded."PesoKg","VolumenM3"=excluded."VolumenM3","Entregados"=excluded."Entregados","Pendientes"=excluded."Pendientes","Devueltos"=excluded."Devueltos";
 DELETE FROM public."GestionMotivos" WHERE "OperadorId"=e."OperadorId" AND "EnvioId"=e."Id";
 INSERT INTO public."GestionMotivos"
 SELECT j."OperadorId",j."EnvioId",j."Id",j."AccesoId",j."RutaId",(j."Payload"->>'ocurridaEn')::timestamptz,CASE WHEN j."Tipo"='RetiroFallido' THEN 'FalloRetiro' ELSE 'IntentoFallido' END,coalesce(j."Payload"->'datos'->>'motivo','Sin motivo registrado'),0
 FROM public."OperacionesJornada" j WHERE j."OperadorId"=e."OperadorId" AND j."EnvioId"=e."Id" AND j."Resultado"='Aplicada' AND j."Tipo" IN ('IntentoFallido','RetiroFallido');
 INSERT INTO public."GestionMotivos"
 SELECT j."OperadorId",j."EnvioId",j."Id",j."AccesoId",j."RutaId",(j."Payload"->>'ocurridaEn')::timestamptz,'PendienteBulto',r->>'motivo',count(*)::int
 FROM public."OperacionesJornada" j CROSS JOIN LATERAL jsonb_array_elements(j."Payload"->'datos'->'resultadosBultos') r
 JOIN public."Bultos" b ON b."OperadorId"=j."OperadorId" AND b."EnvioId"=j."EnvioId" AND b."Id"=(r->>'bultoId')::uuid
 WHERE j."OperadorId"=e."OperadorId" AND j."EnvioId"=e."Id" AND j."Resultado"='Aplicada' AND j."Tipo"='EntregaBultos' AND NOT (r->>'entregado')::boolean
 -- Evidence completions contain only delivered parcels and cannot become failed visits.
 GROUP BY j."OperadorId",j."EnvioId",j."Id",j."AccesoId",j."RutaId",j."Payload",r->>'motivo';
 UPDATE public."GestionPendientes" SET "Aplicada"=q."Generacion","ActualizadaEnUtc"=clock_timestamp() WHERE "OperadorId"=q."OperadorId" AND "EnvioId"=q."EnvioId";
 n:=n+1;
 END LOOP;
 RETURN n;
END $$;
ALTER FUNCTION nexoruta_app.reconcile_management(uuid,uuid,int) OWNER TO nexoruta_job_fn;
REVOKE ALL ON FUNCTION nexoruta_app.reconcile_management(uuid,uuid,int) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nexoruta_app.reconcile_management(uuid,uuid,int) TO nexoruta_worker;
CREATE FUNCTION nexoruta_app.request_management_rebuild(p_operator uuid) RETURNS int LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE n int;
BEGIN
 INSERT INTO public."GestionPendientes"("OperadorId","EnvioId") SELECT "OperadorId","Id" FROM public."Envios" WHERE "OperadorId"=p_operator
 ON CONFLICT("OperadorId","EnvioId") DO UPDATE SET "Generacion"="GestionPendientes"."Generacion"+1;
 GET DIAGNOSTICS n=ROW_COUNT; RETURN n;
END $$;
ALTER FUNCTION nexoruta_app.request_management_rebuild(uuid) OWNER TO nexoruta_job_fn;
REVOKE ALL ON FUNCTION nexoruta_app.request_management_rebuild(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nexoruta_app.request_management_rebuild(uuid) TO nexoruta_worker;
CREATE OR REPLACE FUNCTION nexoruta_app.operational_projection_health(p_projection text,p_partition int,p_partitions int,p_lag bigint) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF p_projection NOT IN ('board','history','management') OR p_partition<0 OR p_partitions<1 OR p_partition>=p_partitions OR p_lag<0 THEN RAISE EXCEPTION 'Invalid projection health'; END IF;
 INSERT INTO public."OperacionProyeccionSalud" VALUES(p_projection,p_partition,p_partitions,p_lag,clock_timestamp())
 ON CONFLICT("Proyeccion","Particion") DO UPDATE SET "Particiones"=excluded."Particiones","Atraso"=excluded."Atraso","VerificadaEnUtc"=excluded."VerificadaEnUtc";
END $$;
-- Initial facts are marked for reproducible worker backfill; migrations do not fabricate history.
CREATE FUNCTION public.bootstrap_management() RETURNS void LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $$
 INSERT INTO public."GestionPendientes"("OperadorId","EnvioId") SELECT "OperadorId","Id" FROM public."Envios";
$$;
ALTER FUNCTION public.bootstrap_management() OWNER TO nexoruta_job_fn;
REVOKE ALL ON FUNCTION public.bootstrap_management() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION public.bootstrap_management() TO CURRENT_USER;
SELECT public.bootstrap_management();
DROP FUNCTION public.bootstrap_management();
CREATE FUNCTION nexoruta_app.management_compliance(deadline timestamptz,done timestamptz,in_slot boolean,proof boolean,excluded boolean) RETURNS text LANGUAGE sql STABLE SET search_path=pg_catalog AS $$
 SELECT CASE WHEN excluded THEN 'NoEvaluable' WHEN deadline IS NULL THEN 'SinCompromiso' WHEN proof THEN 'PruebaPendiente' WHEN done IS NOT NULL THEN CASE WHEN done<=deadline AND in_slot THEN 'Cumplido' ELSE 'Incumplido' END WHEN statement_timestamp()>=deadline THEN 'Incumplido' ELSE 'EnPlazo' END
$$;
ALTER FUNCTION nexoruta_app.management_compliance(timestamptz,timestamptz,boolean,boolean,boolean) OWNER TO nexoruta_schema_owner;
REVOKE ALL ON FUNCTION nexoruta_app.management_compliance(timestamptz,timestamptz,boolean,boolean,boolean) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nexoruta_app.management_compliance(timestamptz,timestamptz,boolean,boolean,boolean) TO nexoruta_api;
DO $m$ DECLARE t text; BEGIN FOREACH t IN ARRAY ARRAY['GestionPendientes','GestionOperaciones','GestionBultos','GestionMotivos'] LOOP EXECUTE format('ALTER TABLE public.%I OWNER TO nexoruta_schema_owner',t); END LOOP; END $m$;
""");
    protected override void Down(MigrationBuilder m) => throw new NotSupportedException("Reconcile derived reports without deleting accepted facts.");
}
