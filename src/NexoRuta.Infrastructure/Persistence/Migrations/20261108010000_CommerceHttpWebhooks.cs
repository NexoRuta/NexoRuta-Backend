using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace NexoRuta.Infrastructure.Persistence.Migrations;
[DbContext(typeof(NexoRutaDbContext))]
[Migration("20261108010000_CommerceHttpWebhooks")]
public sealed class CommerceHttpWebhooks : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""

DO $$ BEGIN
 IF NOT EXISTS(SELECT FROM pg_roles WHERE rolname='nexoruta_webhook_fn') THEN
 CREATE ROLE nexoruta_webhook_fn NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
 END IF;
 IF EXISTS(SELECT FROM pg_roles WHERE rolname='nexoruta_webhook_fn' AND (rolsuper OR rolbypassrls OR rolcanlogin)) THEN RAISE EXCEPTION 'Unsafe webhook role'; END IF;
END $$;
GRANT USAGE ON SCHEMA public,nexoruta_app TO nexoruta_webhook_fn;
GRANT EXECUTE ON FUNCTION nexoruta_app.ctx_uuid(text),nexoruta_app.setting(text) TO nexoruta_webhook_fn;
CREATE TABLE public."WebhookSubscriptions"(
 "Id" uuid PRIMARY KEY,"OperadorId" uuid NOT NULL REFERENCES public."Operadores"("Id"),
 "ComercioId" uuid NOT NULL REFERENCES public."Comercios"("Id"),"Url" varchar(2048) NOT NULL,
 "Eventos" text[] NOT NULL,"Contrato" int NOT NULL CHECK("Contrato" IN(1,2)),"Habilitada" boolean NOT NULL,"Revocada" boolean NOT NULL DEFAULT false CHECK(NOT "Habilitada" OR NOT "Revocada"),
 "Revision" bigint NOT NULL CHECK("Revision">0),"ClaveId" uuid NOT NULL,"SecretoProtegido" text NOT NULL,
 "ActualizadaEnUtc" timestamptz NOT NULL DEFAULT clock_timestamp(),
 CHECK(cardinality("Eventos") BETWEEN 1 AND 8),
 CHECK("Eventos" <@ ARRAY['EnvioCreado','OperacionActualizada','BultoActualizado','NuevaVisita','RetiroSolicitado','RetiroRetirado','RetiroEntregadoComercio']),
 CHECK("Contrato"=2 OR "Eventos" <@ ARRAY['EnvioCreado','OperacionActualizada']));
CREATE TABLE public."WebhookRevisions"(
 "Id" uuid NOT NULL,"Revision" bigint NOT NULL,"OperadorId" uuid NOT NULL,"ComercioId" uuid NOT NULL,
 "Url" text NOT NULL,"Eventos" text[] NOT NULL,"Contrato" int NOT NULL,"Habilitada" boolean NOT NULL,
 "ClaveId" uuid NOT NULL,"Accion" text NOT NULL DEFAULT 'Configuracion',"ActorId" uuid NOT NULL,"FechaUtc" timestamptz NOT NULL DEFAULT clock_timestamp(),
 PRIMARY KEY("Id","Revision"));
CREATE TABLE public."WebhookDeliveries"(
 "Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),"EventoId" uuid NOT NULL REFERENCES public."OutboxMessages"("Id"),
 "SuscripcionId" uuid NOT NULL REFERENCES public."WebhookSubscriptions"("Id"),"Revision" bigint NOT NULL,
 "OperadorId" uuid NOT NULL,"ComercioId" uuid NOT NULL,"EnvioId" uuid NOT NULL,"Tipo" text NOT NULL,
 "Contrato" int NOT NULL,"Url" text NOT NULL,"Payload" text NOT NULL CHECK(octet_length("Payload")<=65536),
 "ClaveId" uuid NOT NULL,"SecretoProtegido" text NOT NULL,
 "Estado" text NOT NULL DEFAULT 'Pendiente' CHECK("Estado" IN('Pendiente','Procesando','Reintento','Entregado','Fallido','Cancelado')),
 "Intentos" int NOT NULL DEFAULT 0,"IntentosCiclo" int NOT NULL DEFAULT 0,"Reenvios" int NOT NULL DEFAULT 0,
 "MaxIntentos" int,"ProximoIntentoUtc" timestamptz NOT NULL DEFAULT clock_timestamp(),
 "CreadaEnUtc" timestamptz NOT NULL DEFAULT clock_timestamp(),"FinalizadaEnUtc" timestamptz,
 "ReservaId" uuid,"ReservaHastaUtc" timestamptz,"CodigoHttp" int,"Diagnostico" varchar(120),
 UNIQUE("EventoId","SuscripcionId"),FOREIGN KEY("SuscripcionId","Revision") REFERENCES public."WebhookRevisions"("Id","Revision"));
CREATE INDEX webhook_due ON public."WebhookDeliveries"("ProximoIntentoUtc","Id") WHERE "Estado" IN('Pendiente','Reintento','Procesando');
CREATE INDEX webhook_history ON public."WebhookDeliveries"("ComercioId","OperadorId","CreadaEnUtc" DESC,"Id");
CREATE TABLE public."WebhookAttempts"(
 "EntregaId" uuid NOT NULL REFERENCES public."WebhookDeliveries"("Id"),"Numero" int NOT NULL,
 "OperadorId" uuid NOT NULL,"ComercioId" uuid NOT NULL,"InicioUtc" timestamptz NOT NULL,
 "FinUtc" timestamptz,"CodigoHttp" int,"Diagnostico" varchar(120),PRIMARY KEY("EntregaId","Numero"));
CREATE TABLE public."WebhookReplays"(
 "EntregaId" uuid NOT NULL REFERENCES public."WebhookDeliveries"("Id"),"Numero" int NOT NULL,
 "OperadorId" uuid NOT NULL,"ComercioId" uuid NOT NULL,"ActorId" uuid NOT NULL,"FechaUtc" timestamptz NOT NULL DEFAULT clock_timestamp(),
 "EstadoAnterior" text NOT NULL,PRIMARY KEY("EntregaId","Numero"));
DO $$ DECLARE t text; BEGIN
 FOREACH t IN ARRAY ARRAY['WebhookSubscriptions','WebhookRevisions','WebhookDeliveries','WebhookAttempts','WebhookReplays'] LOOP
 EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY',t);
 EXECUTE format('ALTER TABLE public.%I FORCE ROW LEVEL SECURITY',t);
 EXECUTE format('GRANT SELECT ON public.%I TO nexoruta_api',t);
 EXECUTE format('GRANT SELECT,INSERT,UPDATE ON public.%I TO nexoruta_webhook_fn',t);
 EXECUTE format('CREATE POLICY webhook_internal ON public.%I TO nexoruta_webhook_fn USING(true) WITH CHECK(true)',t);
 EXECUTE format('CREATE POLICY webhook_read ON public.%I FOR SELECT TO nexoruta_api USING((nexoruta_app.setting(''actor_kind'')=''comercio'' AND "ComercioId"=nexoruta_app.ctx_uuid(''org_id'')) OR (nexoruta_app.setting(''actor_kind'')=''operador'' AND "OperadorId"=nexoruta_app.ctx_uuid(''org_id'')))',t);
 END LOOP;
END $$;
GRANT INSERT,UPDATE ON public."WebhookSubscriptions" TO nexoruta_api;
CREATE POLICY webhook_config ON public."WebhookSubscriptions" FOR ALL TO nexoruta_api
 USING(nexoruta_app.setting('actor_kind')='comercio' AND "ComercioId"=nexoruta_app.ctx_uuid('org_id'))
 WITH CHECK(nexoruta_app.setting('actor_kind')='comercio' AND "ComercioId"=nexoruta_app.ctx_uuid('org_id'));
CREATE FUNCTION nexoruta_app.webhook_revision() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF TG_OP='UPDATE' AND (NEW."Id"<>OLD."Id" OR NEW."OperadorId"<>OLD."OperadorId" OR NEW."ComercioId"<>OLD."ComercioId" OR NEW."Revision"<>OLD."Revision"+1) THEN RAISE EXCEPTION 'Invalid subscription revision'; END IF;
 INSERT INTO public."WebhookRevisions"("Id","Revision","OperadorId","ComercioId","Url","Eventos","Contrato","Habilitada","ClaveId","ActorId")
 VALUES(NEW."Id",NEW."Revision",NEW."OperadorId",NEW."ComercioId",NEW."Url",NEW."Eventos",NEW."Contrato",NEW."Habilitada",NEW."ClaveId",nexoruta_app.ctx_uuid('user_id'));
 RETURN NEW;
END $$;
CREATE TRIGGER webhook_revision AFTER INSERT OR UPDATE ON public."WebhookSubscriptions" FOR EACH ROW EXECUTE FUNCTION nexoruta_app.webhook_revision();
-- A visit is announced once per persisted route/shipment, regardless of later schedule updates.
CREATE TABLE public."WebhookVisitFacts"("RutaId" uuid NOT NULL,"EnvioId" uuid NOT NULL,PRIMARY KEY("RutaId","EnvioId"));
INSERT INTO public."WebhookVisitFacts" SELECT "RutaId","EnvioId" FROM public."ParadasRuta" ON CONFLICT DO NOTHING;
ALTER TABLE public."WebhookVisitFacts" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."WebhookVisitFacts" FORCE ROW LEVEL SECURITY;
GRANT SELECT,INSERT ON public."WebhookVisitFacts" TO nexoruta_webhook_fn;
CREATE POLICY webhook_visit_capture ON public."WebhookVisitFacts" TO nexoruta_webhook_fn USING(true) WITH CHECK(true);
GRANT SELECT ON public."Envios",public."Bultos" TO nexoruta_webhook_fn;
CREATE POLICY webhook_source ON public."Envios" FOR SELECT TO nexoruta_webhook_fn USING(true);
CREATE POLICY webhook_source ON public."Bultos" FOR SELECT TO nexoruta_webhook_fn USING(true);
CREATE FUNCTION nexoruta_app.webhook_capture() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE p jsonb:=NEW."Payload"; s public."WebhookSubscriptions"; shipment uuid; commerce uuid; kind text; body jsonb; fact timestamptz;
 e public."Envios"; progress jsonb; parcel jsonb;
BEGIN
 -- Consume canonical accepted shipment/parcel/visit events; never the Kafka inbox projection.
 IF NEW."Tipo"<>'EventoOperativo' OR p->>'tipo' NOT IN('Envio','Bulto','Parada') OR p->'datos'->>'procedencia'='SnapshotBootstrap' THEN RETURN NEW; END IF;
 shipment:=(p->>'envioId')::uuid;
 SELECT * INTO e FROM public."Envios" WHERE "OperadorId"=NEW."OperadorId" AND "Id"=shipment;
 IF NOT FOUND THEN RETURN NEW; END IF;
 commerce:=e."ComercioId"; fact:=coalesce((p->>'ocurridaEnUtc')::timestamptz,(p->>'registradaEnUtc')::timestamptz);
 IF p->>'tipo'='Envio' THEN
  -- Tariff-only stream events and observed bootstrap snapshots aren't state transitions.
  IF NOT (p->'datos' ? 'origen') THEN RETURN NEW; END IF;
  IF p->'datos'->>'hecho' IS NOT NULL THEN RETURN NEW; END IF;
  kind:=CASE p->'datos'->>'estado' WHEN 'Admitido' THEN 'EnvioCreado' WHEN 'RetiroSolicitado' THEN 'RetiroSolicitado' WHEN 'Retirado' THEN 'RetiroRetirado' WHEN 'EntregadoComercio' THEN 'RetiroEntregadoComercio' ELSE 'OperacionActualizada' END;
 ELSIF p->>'tipo'='Bulto' THEN
  IF (p->>'version')::bigint=0 THEN RETURN NEW; END IF;
  kind:='BultoActualizado';
  SELECT jsonb_build_object('bultoId',b."Id",'codigo',b."Codigo",'resultado',p->'datos'->'resultado','custodia',p->'datos'->'custodia','version',p->'version') INTO parcel
  FROM public."Bultos" b WHERE b."OperadorId"=e."OperadorId" AND b."Id"=(p->>'entidadId')::uuid;
 ELSE
  IF p->'datos'->>'activa'<>'true' OR p->'datos'->>'resultadoVisita'<>'Pendiente' THEN RETURN NEW; END IF;
  INSERT INTO public."WebhookVisitFacts" VALUES((p->>'rutaId')::uuid,shipment) ON CONFLICT DO NOTHING;
  IF NOT FOUND THEN RETURN NEW; END IF;
  kind:='NuevaVisita';
 END IF;
 SELECT jsonb_build_object('total',count(*),'entregados',count(*) FILTER(WHERE "Resultado"='Entregado'),'pendientes',count(*) FILTER(WHERE "Resultado" NOT IN('Entregado','Devuelto','Extraviado'))) INTO progress
 FROM public."Bultos" WHERE "OperadorId"=e."OperadorId" AND "EnvioId"=shipment;
 FOR s IN SELECT * FROM public."WebhookSubscriptions" WHERE "OperadorId"=e."OperadorId" AND "ComercioId"=commerce AND "Habilitada" AND (kind=ANY("Eventos") OR "Contrato"=1 AND kind LIKE 'Retiro%' AND 'OperacionActualizada'=ANY("Eventos")) FOR SHARE LOOP
  IF s."Contrato"=1 THEN
   -- Preserve the original shipment payload shape. Metadata lives in signed HTTP headers.
   IF kind NOT IN('EnvioCreado','OperacionActualizada','RetiroSolicitado','RetiroRetirado','RetiroEntregadoComercio') THEN CONTINUE; END IF;
   body:=jsonb_build_object('envioId',shipment,'operadorId',e."OperadorId",'comercioId',commerce,'estado',CASE WHEN p->>'tipo'='Envio' THEN p->'datos'->>'estado' ELSE e."Estado" END);
  ELSE
   body:=jsonb_build_object('schemaVersion',2,'eventId',NEW."Id",'tipo',kind,'ocurridaEnUtc',fact,'operadorId',e."OperadorId",'comercioId',commerce,'envioId',shipment,
    'estado',CASE WHEN p->>'tipo'='Envio' THEN p->'datos'->>'estado' ELSE e."Estado" END,'versionOperacion',CASE WHEN p->>'tipo'='Envio' THEN (p->>'version')::bigint ELSE e."Version" END,'esRetiro',e."EsRetiro",'progreso',progress,'bulto',parcel,
    'visita',CASE WHEN kind='NuevaVisita' THEN jsonb_build_object('rutaId',p->'rutaId','orden',p->'datos'->'orden') ELSE NULL END);
  END IF;
  INSERT INTO public."WebhookDeliveries"("EventoId","SuscripcionId","Revision","OperadorId","ComercioId","EnvioId","Tipo","Contrato","Url","Payload","ClaveId","SecretoProtegido")
  VALUES(NEW."Id",s."Id",s."Revision",e."OperadorId",commerce,shipment,CASE WHEN s."Contrato"=1 AND kind LIKE 'Retiro%' THEN 'OperacionActualizada' ELSE kind END,s."Contrato",s."Url",body::text,s."ClaveId",s."SecretoProtegido") ON CONFLICT("EventoId","SuscripcionId") DO NOTHING;
 END LOOP;
 RETURN NEW;
END $$;
CREATE CONSTRAINT TRIGGER webhook_capture AFTER INSERT ON public."OutboxMessages" DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION nexoruta_app.webhook_capture();
CREATE FUNCTION nexoruta_app.webhook_claim(p_token uuid,p_max int) RETURNS SETOF public."WebhookDeliveries" LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE d public."WebhookDeliveries";
BEGIN
 IF p_token IS NULL OR p_max NOT BETWEEN 1 AND 20 THEN RAISE EXCEPTION 'Invalid claim'; END IF;
 SELECT * INTO d FROM public."WebhookDeliveries" WHERE "Estado" IN('Pendiente','Reintento','Procesando')
 AND "ProximoIntentoUtc"<=clock_timestamp() AND ("ReservaHastaUtc" IS NULL OR "ReservaHastaUtc"<=clock_timestamp()) ORDER BY "ProximoIntentoUtc","Id" LIMIT 1 FOR UPDATE SKIP LOCKED;
 IF NOT FOUND THEN RETURN; END IF;
 UPDATE public."WebhookAttempts" SET "FinUtc"=clock_timestamp(),"Diagnostico"='RespuestaDesconocida: reserva vencida' WHERE "EntregaId"=d."Id" AND "FinUtc" IS NULL;
 IF d."IntentosCiclo">=coalesce(d."MaxIntentos",p_max) THEN
 UPDATE public."WebhookDeliveries" SET "Estado"='Fallido',"FinalizadaEnUtc"=clock_timestamp(),"ReservaId"=NULL,"ReservaHastaUtc"=NULL,"Diagnostico"='Intentos agotados; resultado previo desconocido' WHERE "Id"=d."Id";
 RETURN;
 END IF;
 UPDATE public."WebhookDeliveries" SET "Estado"='Procesando',"ReservaId"=p_token,"ReservaHastaUtc"=clock_timestamp()+interval '90 seconds',"Intentos"="Intentos"+1,"IntentosCiclo"="IntentosCiclo"+1,"MaxIntentos"=coalesce("MaxIntentos",p_max) WHERE "Id"=d."Id" RETURNING * INTO d;
 INSERT INTO public."WebhookAttempts"("EntregaId","Numero","OperadorId","ComercioId","InicioUtc") VALUES(d."Id",d."Intentos",d."OperadorId",d."ComercioId",clock_timestamp());
 RETURN NEXT d;
END $$;
CREATE FUNCTION nexoruta_app.webhook_finish(p_id uuid,p_token uuid,p_http int,p_error text,p_retry boolean,p_delay int) RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE d public."WebhookDeliveries"; ok boolean:=p_http BETWEEN 200 AND 299;
BEGIN
 IF p_delay NOT BETWEEN 1 AND 86400 THEN RAISE EXCEPTION 'Invalid retry delay'; END IF;
 SELECT * INTO d FROM public."WebhookDeliveries" WHERE "Id"=p_id AND "ReservaId"=p_token AND "ReservaHastaUtc">clock_timestamp() FOR UPDATE;
 IF NOT FOUND THEN RETURN false; END IF;
 UPDATE public."WebhookAttempts" SET "FinUtc"=clock_timestamp(),"CodigoHttp"=p_http,"Diagnostico"=left(p_error,120) WHERE "EntregaId"=p_id AND "Numero"=d."Intentos";
 UPDATE public."WebhookDeliveries" SET "Estado"=CASE WHEN ok THEN 'Entregado' WHEN p_retry AND "IntentosCiclo"<"MaxIntentos" THEN 'Reintento' ELSE 'Fallido' END,
 "CodigoHttp"=p_http,"Diagnostico"=left(p_error,120),"ReservaId"=NULL,"ReservaHastaUtc"=NULL,
 "ProximoIntentoUtc"=clock_timestamp()+make_interval(secs=>p_delay),"FinalizadaEnUtc"=CASE WHEN ok OR NOT p_retry OR "IntentosCiclo">="MaxIntentos" THEN clock_timestamp() END WHERE "Id"=p_id;
 RETURN true;
END $$;
CREATE FUNCTION nexoruta_app.webhook_replay(p_id uuid) RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE d public."WebhookDeliveries";
BEGIN
 SELECT * INTO d FROM public."WebhookDeliveries" WHERE "Id"=p_id AND "Estado" IN('Fallido','Entregado') AND
 ((nexoruta_app.setting('actor_kind')='comercio' AND "ComercioId"=nexoruta_app.ctx_uuid('org_id')) OR (nexoruta_app.setting('actor_kind')='operador' AND "OperadorId"=nexoruta_app.ctx_uuid('org_id'))) FOR UPDATE;
 IF NOT FOUND THEN RETURN false; END IF;
 INSERT INTO public."WebhookReplays"("EntregaId","Numero","OperadorId","ComercioId","ActorId","EstadoAnterior") VALUES(d."Id",d."Reenvios"+1,d."OperadorId",d."ComercioId",nexoruta_app.ctx_uuid('user_id'),d."Estado");
 UPDATE public."WebhookDeliveries" SET "Estado"='Pendiente',"IntentosCiclo"=0,"Reenvios"="Reenvios"+1,"FinalizadaEnUtc"=NULL,"ProximoIntentoUtc"=clock_timestamp() WHERE "Id"=p_id;
 RETURN true;
END $$;
CREATE FUNCTION nexoruta_app.webhook_revoke(p_id uuid,p_revision bigint) RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE s public."WebhookSubscriptions";
BEGIN
 SELECT * INTO s FROM public."WebhookSubscriptions" WHERE "Id"=p_id AND "Revision"=p_revision
 AND nexoruta_app.setting('actor_kind')='comercio' AND "ComercioId"=nexoruta_app.ctx_uuid('org_id') FOR UPDATE;
 IF NOT FOUND THEN RETURN false; END IF;
 UPDATE public."WebhookSubscriptions" SET "Habilitada"=false,"Revocada"=true,"Revision"="Revision"+1,"ActualizadaEnUtc"=clock_timestamp() WHERE "Id"=p_id;
 UPDATE public."WebhookRevisions" SET "Accion"='Revocacion' WHERE "Id"=p_id AND "Revision"=p_revision+1;
 UPDATE public."WebhookAttempts" a SET "FinUtc"=clock_timestamp(),"Diagnostico"='Revocada; posible efecto remoto en curso'
 FROM public."WebhookDeliveries" d WHERE a."EntregaId"=d."Id" AND d."SuscripcionId"=p_id AND a."FinUtc" IS NULL;
 UPDATE public."WebhookDeliveries" SET "Estado"='Cancelado',"ReservaId"=NULL,"ReservaHastaUtc"=NULL,"FinalizadaEnUtc"=clock_timestamp(),"Diagnostico"='Clave revocada por el comercio'
 WHERE "SuscripcionId"=p_id AND "Estado" IN('Pendiente','Reintento','Procesando');
 RETURN true;
END $$;
DO $$ DECLARE f record; BEGIN
 FOR f IN SELECT oid::regprocedure AS name FROM pg_proc WHERE pronamespace='nexoruta_app'::regnamespace AND proname LIKE 'webhook_%' LOOP
 EXECUTE format('ALTER FUNCTION %s OWNER TO nexoruta_webhook_fn',f.name);
 EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC',f.name);
 END LOOP;
END $$;
GRANT EXECUTE ON FUNCTION nexoruta_app.webhook_claim(uuid,int),nexoruta_app.webhook_finish(uuid,uuid,int,text,boolean,int) TO nexoruta_worker;
GRANT EXECUTE ON FUNCTION nexoruta_app.webhook_replay(uuid),nexoruta_app.webhook_revoke(uuid,bigint) TO nexoruta_api;

""");
    protected override void Down(MigrationBuilder m) => throw new NotSupportedException("Webhook audit and pending deliveries must be retained.");
}
