using Microsoft.EntityFrameworkCore.Migrations;
namespace NexoRuta.Infrastructure.Persistence.Migrations;
public partial class ParcelResultsAndHomePickups
{
 private static void InstalarCircuitos(MigrationBuilder m) => m.Sql("""
CREATE OR REPLACE FUNCTION public.guard_shipment_state_transition() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF NEW."Estado" IS NOT DISTINCT FROM OLD."Estado" THEN RETURN NEW; END IF;
 IF (OLD."Estado" IN ('Admitido') AND NEW."Estado" IN ('EnDeposito','Cancelado'))
 OR (OLD."Estado" IN ('EnDeposito') AND NEW."Estado" IN ('AsignadoRuta','Extraviado','Cancelado'))
 OR (OLD."Estado" IN ('AsignadoRuta') AND NEW."Estado" IN ('EnTransito','Extraviado','Cancelado'))
 OR (OLD."Estado" IN ('EnTransito') AND NEW."Estado" IN ('Entregado','EntregaPendienteValidacion','NoEntregado','Extraviado','Cancelado'))
 OR (OLD."Estado" IN ('NoEntregado') AND NEW."Estado" IN ('Reprogramado','EnDevolucion','Cancelado'))
 OR (OLD."Estado" IN ('Reprogramado') AND NEW."Estado" IN ('AsignadoRuta','Cancelado'))
 OR (OLD."Estado" IN ('EnDevolucion') AND NEW."Estado" IN ('Devuelto','Extraviado','Cancelado'))
 OR (OLD."Estado" IN ('EntregaPendienteValidacion') AND NEW."Estado" IN ('Entregado','EntregaSinValidacion'))
 OR (OLD."Estado" IN ('EnTransito') AND NEW."Estado" IN ('EntregadoParcialmente'))
 OR (OLD."Estado" IN ('EntregadoParcialmente') AND NEW."Estado" IN ('Entregado','EntregaPendienteValidacion','EntregaSinValidacion'))
 OR (OLD."Estado" IN ('Admitido') AND NEW."Estado" IN ('RetiroSolicitado'))
 OR (OLD."Estado" IN ('RetiroSolicitado','RetiroFallido') AND NEW."Estado" IN ('RetiroAsignado','Cancelado'))
 OR (OLD."Estado" IN ('RetiroAsignado') AND NEW."Estado" IN ('Retirado','RetiroFallido','Cancelado'))
 OR (OLD."Estado" IN ('Retirado') AND NEW."Estado" IN ('RetiroEnDeposito'))
 OR (OLD."Estado" IN ('RetiroEnDeposito') AND NEW."Estado" IN ('EntregadoComercio')) THEN RETURN NEW; END IF;
 RAISE EXCEPTION 'Shipment state transition not allowed' USING ERRCODE='23514', CONSTRAINT='CK_Envios_TransicionEstado';
END $$;
GRANT UPDATE ("Resultado","MotivoNoEntrega","EntregadoEnUtc","PruebaVenceEnUtc") ON public."Bultos" TO nexoruta_api;
GRANT UPDATE ("EsRetiro","MotivoRetiro","FranjaRetiroDesdeUtc","FranjaRetiroHastaUtc","SolicitudRetiroId","SolicitudRetiroHuella","Estado","Version") ON public."Envios" TO nexoruta_shipment_fn;
GRANT SELECT ON public."Envios" TO nexoruta_shipment_fn;
CREATE POLICY pickup_created_select ON public."Envios" FOR SELECT TO nexoruta_shipment_fn
 USING (nexoruta_app.setting('actor_kind')='comercio' AND "ComercioId"=nexoruta_app.ctx_uuid('org_id') AND "Id"=nexoruta_app.ctx_uuid('shipment_id'));
CREATE POLICY pickup_created_update ON public."Envios" FOR UPDATE TO nexoruta_shipment_fn
 USING (nexoruta_app.setting('actor_kind')='comercio' AND "ComercioId"=nexoruta_app.ctx_uuid('org_id') AND "Id"=nexoruta_app.ctx_uuid('shipment_id') AND "Estado"='Admitido' AND NOT "EsRetiro")
 WITH CHECK ("ComercioId"=nexoruta_app.ctx_uuid('org_id') AND "Id"=nexoruta_app.ctx_uuid('shipment_id') AND "Estado"='RetiroSolicitado' AND "EsRetiro");
CREATE FUNCTION nexoruta_app.configure_pickup(p_envio uuid,p_solicitud uuid,p_motivo text,p_desde timestamptz,p_hasta timestamptz,p_huella text)
 RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
 BEGIN
  IF p_solicitud IS NULL OR p_solicitud='00000000-0000-0000-0000-000000000000'::uuid OR nullif(btrim(p_motivo),'') IS NULL
    OR length(p_motivo)>500 OR p_desde IS NULL OR p_hasta<=p_desde OR p_huella !~ '^[A-F0-9]{64}$' THEN RAISE EXCEPTION 'Invalid pickup request' USING ERRCODE='22023'; END IF;
  UPDATE public."Envios" SET "EsRetiro"=true,"SolicitudRetiroId"=p_solicitud,"SolicitudRetiroHuella"=p_huella,
    "MotivoRetiro"=p_motivo,"FranjaRetiroDesdeUtc"=p_desde,"FranjaRetiroHastaUtc"=p_hasta,"Estado"='RetiroSolicitado',"Version"="Version"+1
    WHERE "Id"=p_envio AND "OperadorId"=nexoruta_app.ctx_uuid('selected_operator_id');
  IF NOT FOUND THEN RAISE EXCEPTION 'Pickup creation context unavailable' USING ERRCODE='42501'; END IF;
 END $$;
ALTER FUNCTION nexoruta_app.configure_pickup(uuid,uuid,text,timestamptz,timestamptz,text) OWNER TO nexoruta_shipment_fn;
REVOKE ALL ON FUNCTION nexoruta_app.configure_pickup(uuid,uuid,text,timestamptz,timestamptz,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nexoruta_app.configure_pickup(uuid,uuid,text,timestamptz,timestamptz,text) TO nexoruta_api;

-- Observe legacy results using only known facts. Never create a physical receipt or transfer.
UPDATE public."Bultos" b SET "Resultado"=CASE
 WHEN e."Estado"='Devuelto' THEN 'Devuelto'
 WHEN b."Custodia"='Destinatario' AND e."Estado" IN ('Entregado','EntregaPendienteValidacion','EntregaSinValidacion') THEN e."Estado"
 ELSE 'Pendiente' END,
 "EntregadoEnUtc"=CASE WHEN b."Custodia"='Destinatario' THEN e."EntregaFisicaEnUtc" END,
 "PruebaVenceEnUtc"=CASE WHEN b."Custodia"='Destinatario' THEN e."PlazoPruebaVenceEnUtc" END
 FROM public."Envios" e WHERE e."OperadorId"=b."OperadorId" AND e."Id"=b."EnvioId";

CREATE FUNCTION public.guard_parcel_result_transition() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF OLD."Resultado"=NEW."Resultado" THEN RETURN NEW; END IF;
 IF (OLD."Resultado"='Pendiente' AND NEW."Resultado" IN ('Entregado','EntregaPendienteValidacion','EnDevolucion'))
 OR (OLD."Resultado"='EntregaPendienteValidacion' AND NEW."Resultado" IN ('Entregado','EntregaSinValidacion'))
 OR (OLD."Resultado"='EnDevolucion' AND NEW."Resultado"='Devuelto') THEN RETURN NEW; END IF;
 RAISE EXCEPTION 'Parcel result transition not allowed' USING ERRCODE='23514';
END $$;
ALTER FUNCTION public.guard_parcel_result_transition() OWNER TO nexoruta_schema_owner;
CREATE TRIGGER parcel_result_matrix BEFORE UPDATE OF "Resultado" ON public."Bultos" FOR EACH ROW EXECUTE FUNCTION public.guard_parcel_result_transition();

-- Public read reuses the exact token authorization; counts expose no internal parcel or tenant IDs.
GRANT SELECT ON public."Bultos" TO nexoruta_public_fn;
CREATE POLICY parcel_public_progress ON public."Bultos" FOR SELECT TO nexoruta_public_fn
 USING (EXISTS (SELECT 1 FROM public."Envios" e WHERE e."OperadorId"="Bultos"."OperadorId" AND e."Id"="Bultos"."EnvioId"));
CREATE FUNCTION nexoruta_app.public_parcel_progress(p_token text)
 RETURNS TABLE("Total" integer,"Entregados" integer,"Devueltos" integer,"EsRetiro" boolean)
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF p_token IS NULL OR p_token !~ '^[0-9a-f]{64}$' THEN RETURN; END IF;
 PERFORM set_config('nexoruta.public_token_hash',encode(public.digest(convert_to(p_token,'SQL_ASCII'),'sha256'),'hex'),true);
 RETURN QUERY SELECT count(b."Id")::integer,count(b."Id") FILTER(WHERE b."Resultado"='Entregado')::integer,
   count(b."Id") FILTER(WHERE b."Resultado"='Devuelto')::integer,e."EsRetiro"
 FROM public."Envios" e JOIN public."Bultos" b ON b."OperadorId"=e."OperadorId" AND b."EnvioId"=e."Id"
 WHERE EXISTS(SELECT 1 FROM public."EnlacesSeguimiento" l WHERE l."OperadorId"=e."OperadorId" AND l."EnvioId"=e."Id"
  AND l."TokenHash"=nexoruta_app.setting('public_token_hash') AND l."RevocadoEnUtc" IS NULL AND l."ExpiraEnUtc">now())
 GROUP BY e."Id",e."EsRetiro";
END $$;
ALTER FUNCTION nexoruta_app.public_parcel_progress(text) OWNER TO nexoruta_public_fn;
REVOKE ALL ON FUNCTION nexoruta_app.public_parcel_progress(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nexoruta_app.public_parcel_progress(text) TO nexoruta_api;

ALTER TABLE public."OutboxMessages" DROP CONSTRAINT "CK_OutboxMessages_Contract";
ALTER TABLE public."OutboxMessages" ADD CONSTRAINT "CK_OutboxMessages_Contract" CHECK (("Tipo" = 'GenerarSeguimiento' AND "VersionContrato" = 1 AND "Destino" = 'RabbitMq' AND "Ambito" = 'Envio' AND "EstadoEntrega" IS NULL) OR ("Tipo" = 'EnvioCreado' AND "VersionContrato" = 1 AND "Destino" = 'Kafka' AND "Ambito" = 'Envio' AND "EstadoEntrega" IS NULL) OR ("Tipo" = 'RepartoModificado' AND "VersionContrato" = 1 AND "Destino" = 'PushFcm' AND "Ambito" = 'Envio' AND "EstadoEntrega" IS NULL) OR ("Tipo" = 'CodigoCancelacionEmail' AND "VersionContrato" = 1 AND "Destino" = 'RabbitMq' AND "Ambito" = 'Envio' AND "EstadoEntrega" IS NULL) OR ("Tipo" = 'PasswordRecoveryEmail' AND "VersionContrato" = 1 AND "Destino" = 'RabbitMq' AND "Ambito" = 'Identidad' AND "EstadoEntrega" IN ('Pending', 'Delivered', 'Expired', 'Failed')) OR ("Tipo" = 'PasswordRecoveryRequested' AND "VersionContrato" = 1 AND "Destino" = 'Kafka' AND "Ambito" = 'Identidad' AND "EstadoEntrega" IS NULL) OR ("Tipo" IN ('BultoActualizado','OperacionActualizada') AND "VersionContrato"=2 AND "Destino"='Kafka' AND "Ambito"='Envio' AND "EstadoEntrega" IS NULL));
GRANT INSERT ON public."OutboxMessages" TO nexoruta_history_fn;
GRANT SELECT ON public."Envios" TO nexoruta_history_fn;
CREATE POLICY history_notices_envio ON public."Envios" FOR SELECT TO nexoruta_history_fn USING(true);
CREATE POLICY history_notices_outbox ON public."OutboxMessages" FOR INSERT TO nexoruta_history_fn
 WITH CHECK("Tipo" IN ('BultoActualizado','OperacionActualizada') AND "VersionContrato"=2 AND "Destino"='Kafka' AND "Ambito"='Envio');
CREATE FUNCTION public.capture_commerce_notice() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c uuid; payload jsonb; shipment uuid; notice text;
BEGIN
 IF TG_TABLE_NAME='Bultos' THEN
  IF NEW."Version"=OLD."Version" THEN RETURN NEW; END IF;
  shipment:=NEW."EnvioId"; notice:='BultoActualizado';
  payload:=jsonb_build_object('bultoId',NEW."Id",'codigo',NEW."Codigo",'version',NEW."Version",'resultado',NEW."Resultado",'custodia',NEW."Custodia",'motivo',NEW."MotivoNoEntrega");
 ELSE
  IF NEW."Estado"=OLD."Estado" THEN RETURN NEW; END IF;
  shipment:=NEW."Id"; notice:='OperacionActualizada';
  payload:=jsonb_build_object('version',NEW."Version",'estado',NEW."Estado",'esRetiro',NEW."EsRetiro");
 END IF;
 SELECT "ComercioId" INTO STRICT c FROM public."Envios" WHERE "OperadorId"=NEW."OperadorId" AND "Id"=shipment;
 INSERT INTO public."OutboxMessages"("Id","OperadorId","EnvioId","Ambito","ClaveAmbito","Tipo","VersionContrato","Destino","Prioridad","Estado","Payload","Intentos","CreadoEnUtc","ProximoIntentoEnUtc")
 VALUES(gen_random_uuid(),NEW."OperadorId",shipment,'Envio',shipment,notice,2,'Kafka','Normal','Pendiente',
  payload||jsonb_build_object('schemaVersion',2,'envioId',shipment,'comercioId',c,'operadorId',NEW."OperadorId"),0,clock_timestamp(),clock_timestamp());
 RETURN NEW;
END $$;
ALTER FUNCTION public.capture_commerce_notice() OWNER TO nexoruta_history_fn;
REVOKE ALL ON FUNCTION public.capture_commerce_notice() FROM PUBLIC;
CREATE TRIGGER parcel_notice_outbox AFTER UPDATE ON public."Bultos" FOR EACH ROW EXECUTE FUNCTION public.capture_commerce_notice();
CREATE TRIGGER operation_notice_outbox AFTER UPDATE ON public."Envios" FOR EACH ROW EXECUTE FUNCTION public.capture_commerce_notice();
CREATE TABLE public."AvisosComercio"("EventoId" uuid PRIMARY KEY,"OperadorId" uuid NOT NULL,"ComercioId" uuid NOT NULL,"EnvioId" uuid NOT NULL,"Tipo" text NOT NULL,"VersionContrato" int NOT NULL CHECK("VersionContrato"=2),"Datos" jsonb NOT NULL,"RecibidoEnUtc" timestamptz NOT NULL DEFAULT clock_timestamp(),
 FOREIGN KEY("OperadorId","EnvioId") REFERENCES public."Envios"("OperadorId","Id"),FOREIGN KEY("ComercioId","EnvioId") REFERENCES public."Envios"("ComercioId","Id"));
ALTER TABLE public."AvisosComercio" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."AvisosComercio" FORCE ROW LEVEL SECURITY;
GRANT SELECT ON public."AvisosComercio" TO nexoruta_api;
CREATE POLICY commerce_notice_read ON public."AvisosComercio" FOR SELECT TO nexoruta_api USING(
 nexoruta_app.setting('actor_kind')='comercio' AND "ComercioId"=nexoruta_app.ctx_uuid('org_id') OR nexoruta_app.setting('actor_kind')='operador' AND "OperadorId"=nexoruta_app.ctx_uuid('org_id'));
GRANT SELECT ON public."OutboxMessages" TO nexoruta_job_fn;
CREATE POLICY job_notice_lookup ON public."OutboxMessages" FOR SELECT TO nexoruta_job_fn USING("Tipo" IN ('BultoActualizado','OperacionActualizada') AND "VersionContrato"=2 AND "Destino"='Kafka');
GRANT INSERT,SELECT ON public."AvisosComercio" TO nexoruta_job_fn;
CREATE POLICY job_notice_insert ON public."AvisosComercio" FOR INSERT TO nexoruta_job_fn WITH CHECK(true);
CREATE POLICY job_notice_select ON public."AvisosComercio" FOR SELECT TO nexoruta_job_fn USING(true);
CREATE FUNCTION nexoruta_app.receive_commerce_notice(p_event uuid,p_payload jsonb) RETURNS boolean
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE m public."OutboxMessages";
BEGIN
 SELECT * INTO m FROM public."OutboxMessages" WHERE "Id"=p_event;
 IF NOT FOUND OR m."Payload"<>p_payload THEN RAISE EXCEPTION 'Unverified commerce event' USING ERRCODE='42501'; END IF;
 INSERT INTO public."AvisosComercio"("EventoId","OperadorId","ComercioId","EnvioId","Tipo","VersionContrato","Datos")
 VALUES(m."Id",m."OperadorId",(m."Payload"->>'comercioId')::uuid,m."EnvioId",m."Tipo",2,m."Payload") ON CONFLICT("EventoId") DO NOTHING;
 RETURN true;
END $$;
ALTER FUNCTION nexoruta_app.receive_commerce_notice(uuid,jsonb) OWNER TO nexoruta_job_fn;
REVOKE ALL ON FUNCTION nexoruta_app.receive_commerce_notice(uuid,jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nexoruta_app.receive_commerce_notice(uuid,jsonb) TO nexoruta_worker;

GRANT SELECT, UPDATE("Resultado","Version") ON public."Bultos" TO nexoruta_proof_deadline_fn;
CREATE POLICY parcel_deadline_scan ON public."Bultos" FOR SELECT TO nexoruta_proof_deadline_fn
 USING (nexoruta_app.setting('actor_kind')='worker' AND nexoruta_app.setting('history_process') LIKE 'worker:proof-validation-deadline%');
CREATE POLICY parcel_deadline_update ON public."Bultos" FOR UPDATE TO nexoruta_proof_deadline_fn
 USING (nexoruta_app.setting('actor_kind')='worker' AND nexoruta_app.setting('history_process')='worker:proof-validation-deadline'
  AND "OperadorId"=nexoruta_app.ctx_uuid('org_id') AND "EnvioId"=nexoruta_app.ctx_uuid('job_shipment_id') AND "Resultado"='EntregaPendienteValidacion')
 WITH CHECK("Resultado"='EntregaSinValidacion');
ALTER POLICY shipment_proof_deadline_fn_scan ON public."Envios" USING(nexoruta_app.setting('actor_kind')='worker' AND nexoruta_app.setting('history_process') LIKE 'worker:proof-validation-deadline%');
ALTER POLICY shipment_proof_deadline_fn_update ON public."Envios"
 USING(nexoruta_app.setting('actor_kind')='worker' AND nexoruta_app.setting('history_process')='worker:proof-validation-deadline'
  AND "OperadorId"=nexoruta_app.ctx_uuid('org_id') AND "Id"=nexoruta_app.ctx_uuid('job_shipment_id') AND "Estado" IN ('EntregaPendienteValidacion','EntregadoParcialmente'))
 WITH CHECK("Estado" IN ('EntregaSinValidacion','EntregadoParcialmente'));
CREATE OR REPLACE FUNCTION nexoruta_app.expire_pending_proof_deadlines(p_now timestamptz,p_batch_size integer)
 RETURNS integer LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c record; n int; total int:=0; nuevo text;
BEGIN
 IF p_now IS NULL OR p_batch_size NOT BETWEEN 1 AND 500 THEN RAISE EXCEPTION 'Invalid deadline sweep input' USING ERRCODE='22023'; END IF;
 PERFORM set_config('nexoruta.actor_kind','worker',true);
 PERFORM set_config('nexoruta.history_process','worker:proof-validation-deadline:scan',true);
 FOR c IN SELECT e."OperadorId",e."Id" FROM public."Envios" e WHERE e."Estado" IN ('EntregaPendienteValidacion','EntregadoParcialmente')
   AND (EXISTS(SELECT 1 FROM public."Bultos" b WHERE b."EnvioId"=e."Id" AND b."OperadorId"=e."OperadorId" AND b."Resultado"='EntregaPendienteValidacion' AND b."PruebaVenceEnUtc"<=p_now)
     OR e."Estado"='EntregaPendienteValidacion' AND e."PlazoPruebaVenceEnUtc"<=p_now AND NOT EXISTS(SELECT 1 FROM public."Bultos" b WHERE b."EnvioId"=e."Id" AND b."Resultado"<>'Pendiente'))
   ORDER BY e."Id" LIMIT p_batch_size
 LOOP
  PERFORM set_config('nexoruta.history_process','worker:proof-validation-deadline',true);
  PERFORM set_config('nexoruta.org_id',c."OperadorId"::text,true); PERFORM set_config('nexoruta.job_shipment_id',c."Id"::text,true);
  PERFORM set_config('nexoruta.history_source_type','PlazoValidacionPrueba',true); PERFORM set_config('nexoruta.history_source_id',c."Id"::text,true);
  PERFORM set_config('nexoruta.history_correlation_id',c."Id"::text,true); PERFORM set_config('nexoruta.history_operation_id','',true);
  PERFORM set_config('nexoruta.history_occurred_at',p_now::text,true); PERFORM set_config('nexoruta.history_fact_code','PruebaBultoVencida',true);
  PERFORM 1 FROM public."Envios" WHERE "OperadorId"=c."OperadorId" AND "Id"=c."Id" FOR UPDATE;
  UPDATE public."Bultos" SET "Resultado"='EntregaSinValidacion',"Version"="Version"+1 WHERE "OperadorId"=c."OperadorId" AND "EnvioId"=c."Id" AND "Resultado"='EntregaPendienteValidacion' AND "PruebaVenceEnUtc"<=p_now;
  UPDATE public."Envios" SET "Estado"=CASE WHEN "Estado"='EntregadoParcialmente' THEN "Estado" ELSE 'EntregaSinValidacion' END,"Version"="Version"+1
   WHERE "OperadorId"=c."OperadorId" AND "Id"=c."Id" AND "Estado" IN ('EntregaPendienteValidacion','EntregadoParcialmente');
  GET DIAGNOSTICS n=ROW_COUNT; total:=total+n;
  PERFORM set_config('nexoruta.history_process','worker:proof-validation-deadline:scan',true);
 END LOOP;
 RETURN total;
END $$;

CREATE UNIQUE INDEX partial_visit_fee_once ON public."TarifasEnvio"("OperadorId","EnvioId",("Servicio"->>'RutaRecargoId')) WHERE "Servicio" ? 'RutaRecargoId';
CREATE POLICY partial_visit_fee_insert ON public."TarifasEnvio" FOR INSERT TO nexoruta_api
 WITH CHECK(nexoruta_app.setting('actor_kind')='operador' AND "OperadorId"=nexoruta_app.ctx_uuid('org_id')
 AND "Servicio" ? 'RutaRecargoId' AND EXISTS(SELECT 1 FROM public."HojasRuta" h WHERE h."OperadorId"="TarifasEnvio"."OperadorId" AND h."Id"=("TarifasEnvio"."Servicio"->>'RutaRecargoId')::uuid AND h."Estado"='Despachada'));
CREATE FUNCTION nexoruta_app.apply_partial_visit_fee(p_shipment uuid,p_route uuid) RETURNS void
 LANGUAGE plpgsql SET search_path=pg_catalog AS $$
DECLARE fee numeric; rules uuid; previous public."TarifasEnvio"; result jsonb;
BEGIN
 SELECT r."RecargoNuevaVisitaUyu",r."Id" INTO fee,rules FROM public."Envios" e JOIN public."VersionesReglasOperativas" r ON r."OperadorId"=e."OperadorId" AND r."Id"=e."VersionReglasId"
 WHERE e."Id"=p_shipment AND e."Estado"='EntregadoParcialmente' AND r."PoliticaPendientesParcial"='NuevaVisitaConRecargo';
 IF NOT FOUND THEN RETURN; END IF;
 IF EXISTS(SELECT 1 FROM public."TarifasEnvio" WHERE "EnvioId"=p_shipment AND "Servicio"->>'RutaRecargoId'=p_route::text) THEN RETURN; END IF;
 SELECT * INTO STRICT previous FROM public."TarifasEnvio" WHERE "EnvioId"=p_shipment ORDER BY "CreadoEnUtc" DESC,"Id" DESC LIMIT 1;
 result:=previous."Resultado" || jsonb_build_object(
  'Ajustes',previous."Resultado"->'Ajustes'||jsonb_build_array(jsonb_build_object('Nombre','Nueva visita entrega parcial','Tipo','RecargoFijo','Base','ImporteFijo','ValorConfigurado',fee,'ImporteUyu',fee)),
  'TotalRecargosUyu',(previous."Resultado"->>'TotalRecargosUyu')::numeric+fee,
  'TotalExactoUyu',(previous."Resultado"->>'TotalExactoUyu')::numeric+fee,
  'TotalFinalUyu',(previous."Resultado"->>'TotalFinalUyu')::numeric+fee);
 INSERT INTO public."TarifasEnvio"("Id","OperadorId","EnvioId","VersionTarifarioId","Servicio","Resultado","Cobertura")
 VALUES(gen_random_uuid(),previous."OperadorId",p_shipment,previous."VersionTarifarioId",
  previous."Servicio"||jsonb_build_object('RutaRecargoId',p_route,'VersionReglasRecargoId',rules),result,previous."Cobertura");
END $$;
ALTER FUNCTION nexoruta_app.apply_partial_visit_fee(uuid,uuid) OWNER TO nexoruta_schema_owner;
REVOKE ALL ON FUNCTION nexoruta_app.apply_partial_visit_fee(uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nexoruta_app.apply_partial_visit_fee(uuid,uuid) TO nexoruta_api;

""");
}
