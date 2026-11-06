using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace NexoRuta.Infrastructure.Persistence.Migrations;

[DbContext(typeof(NexoRutaDbContext))]
[Migration("20261106190400_RecipientStateNotifications")]
public sealed class RecipientStateNotifications : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
DO $m$ DECLARE definition text; BEGIN
 SELECT pg_get_constraintdef(oid) INTO definition FROM pg_constraint WHERE conrelid='public."OutboxMessages"'::regclass AND conname='CK_OutboxMessages_Contract';
 EXECUTE 'ALTER TABLE public."OutboxMessages" DROP CONSTRAINT "CK_OutboxMessages_Contract"';
 EXECUTE 'ALTER TABLE public."OutboxMessages" ADD CONSTRAINT "CK_OutboxMessages_Contract" CHECK (('
 || substring(definition from 8 for length(definition)-8)
 || ') OR ("Tipo"=''AvisoDestinatarioEstado'' AND "VersionContrato"=1 AND "Destino"=''RabbitMq'' AND "Ambito"=''Envio'' AND "EstadoEntrega" IS NULL))';
END $m$;
CREATE TABLE public."AvisosDestinatarioEstado"("Id" uuid PRIMARY KEY,"EventoId" uuid NOT NULL UNIQUE,"OperadorId" uuid NOT NULL,"EnvioId" uuid NOT NULL,"Estado" text NOT NULL,"CorrelacionId" uuid,"EnviadoEnUtc" timestamptz,
 FOREIGN KEY("OperadorId","EnvioId") REFERENCES public."Envios"("OperadorId","Id"));
ALTER TABLE public."AvisosDestinatarioEstado" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."AvisosDestinatarioEstado" FORCE ROW LEVEL SECURITY;
GRANT INSERT,SELECT ON public."AvisosDestinatarioEstado" TO nexoruta_history_fn;
CREATE POLICY recipient_history_existing ON public."AvisosDestinatarioEstado" FOR SELECT TO nexoruta_history_fn USING(true);
CREATE POLICY recipient_history_capture ON public."AvisosDestinatarioEstado" FOR INSERT TO nexoruta_history_fn WITH CHECK(true);
CREATE POLICY recipient_history_outbox ON public."OutboxMessages" FOR INSERT TO nexoruta_history_fn
 WITH CHECK("Tipo"='AvisoDestinatarioEstado' AND "VersionContrato"=1 AND "Destino"='RabbitMq' AND "Ambito"='Envio');
GRANT SELECT ON public."OutboxMessages" TO nexoruta_history_fn;
CREATE POLICY recipient_tracking_lookup ON public."OutboxMessages" FOR SELECT TO nexoruta_history_fn USING("Tipo"='GenerarSeguimiento');
CREATE POLICY recipient_event_lookup ON public."EnvioEventos" FOR SELECT TO nexoruta_history_fn USING(true);
CREATE FUNCTION public.capture_recipient_state_notice() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE notice_id uuid:=gen_random_uuid(); source public."EnvioEventos";
BEGIN
 IF TG_TABLE_NAME='OutboxMessages' THEN
  SELECT * INTO source FROM public."EnvioEventos" WHERE "OperadorId"=NEW."OperadorId" AND "EnvioId"=NEW."EnvioId" ORDER BY "VersionNueva" DESC,"RegistradaEnUtc" DESC,"Id" DESC LIMIT 1;
  IF NOT FOUND THEN RETURN NEW; END IF;
 ELSE
  source:=NEW;
  IF NOT EXISTS(SELECT 1 FROM public."OutboxMessages" WHERE "OperadorId"=source."OperadorId" AND "EnvioId"=source."EnvioId" AND "Tipo"='GenerarSeguimiento') THEN RETURN NEW; END IF;
 END IF;
 IF source."Tipo" NOT IN ('Creacion','TransicionEstado') OR source."EstadoNuevo" NOT IN (
 'Admitido','EnDeposito','EnTransito','Entregado','EntregadoParcialmente','EntregaPendienteValidacion','EntregaSinValidacion','NoEntregado','Reprogramado','Cancelado','Extraviado','EnDevolucion','Devuelto','RetiroSolicitado','RetiroAsignado','Retirado','RetiroFallido','RetiroEnDeposito','EntregadoComercio') THEN RETURN NEW; END IF;
 INSERT INTO public."AvisosDestinatarioEstado"("Id","EventoId","OperadorId","EnvioId","Estado","CorrelacionId") VALUES(notice_id,source."Id",source."OperadorId",source."EnvioId",source."EstadoNuevo",source."CorrelacionId") ON CONFLICT("EventoId") DO NOTHING;
 IF NOT FOUND THEN RETURN NEW; END IF;
 INSERT INTO public."OutboxMessages"("Id","OperadorId","EnvioId","Ambito","ClaveAmbito","Tipo","VersionContrato","Destino","Prioridad","Estado","Payload","Intentos","CreadoEnUtc","ProximoIntentoEnUtc")
 VALUES(notice_id,source."OperadorId",source."EnvioId",'Envio',source."EnvioId",'AvisoDestinatarioEstado',1,'RabbitMq','Normal','Pendiente',jsonb_build_object('eventoId',source."Id",'envioId',source."EnvioId",'operadorId',source."OperadorId",'correlacionId',source."CorrelacionId"),0,clock_timestamp(),clock_timestamp());
 RETURN NEW;
END $$;
ALTER FUNCTION public.capture_recipient_state_notice() OWNER TO nexoruta_history_fn;
REVOKE ALL ON FUNCTION public.capture_recipient_state_notice() FROM PUBLIC;
CREATE TRIGGER recipient_initial_notice AFTER INSERT ON public."OutboxMessages" FOR EACH ROW WHEN (NEW."Tipo"='GenerarSeguimiento') EXECUTE FUNCTION public.capture_recipient_state_notice();
CREATE TRIGGER recipient_state_notice AFTER INSERT ON public."EnvioEventos" FOR EACH ROW EXECUTE FUNCTION public.capture_recipient_state_notice();

GRANT SELECT,UPDATE ON public."AvisosDestinatarioEstado" TO nexoruta_job_fn;
CREATE POLICY recipient_notice_job ON public."AvisosDestinatarioEstado" TO nexoruta_job_fn USING("Id"=nexoruta_app.ctx_uuid('recipient_notice_id')) WITH CHECK("Id"=nexoruta_app.ctx_uuid('recipient_notice_id'));
GRANT SELECT ON public."Envios",public."Destinatarios",public."Operadores",public."VersionesIdentidadVisualOperador",public."EntregasSeguimiento",public."EnlacesSeguimiento" TO nexoruta_job_fn;
CREATE POLICY recipient_notice_shipment ON public."Envios" FOR SELECT TO nexoruta_job_fn USING("Id"=nexoruta_app.ctx_uuid('recipient_notice_shipment') AND "OperadorId"=nexoruta_app.ctx_uuid('recipient_notice_operator'));
CREATE POLICY recipient_notice_recipient ON public."Destinatarios" FOR SELECT TO nexoruta_job_fn USING("OperadorId"=nexoruta_app.ctx_uuid('recipient_notice_operator') AND EXISTS(SELECT 1 FROM public."Envios" e WHERE e."DestinatarioId"="Destinatarios"."Id"));
CREATE POLICY recipient_notice_operator ON public."Operadores" FOR SELECT TO nexoruta_job_fn USING("Id"=nexoruta_app.ctx_uuid('recipient_notice_operator'));
CREATE POLICY recipient_notice_brand ON public."VersionesIdentidadVisualOperador" FOR SELECT TO nexoruta_job_fn USING("OperadorId"=nexoruta_app.ctx_uuid('recipient_notice_operator'));
CREATE POLICY recipient_notice_delivery ON public."EntregasSeguimiento" FOR SELECT TO nexoruta_job_fn USING("EnvioId"=nexoruta_app.ctx_uuid('recipient_notice_shipment') AND "OperadorId"=nexoruta_app.ctx_uuid('recipient_notice_operator'));
CREATE POLICY recipient_notice_link ON public."EnlacesSeguimiento" FOR SELECT TO nexoruta_job_fn USING("EnvioId"=nexoruta_app.ctx_uuid('recipient_notice_shipment') AND "OperadorId"=nexoruta_app.ctx_uuid('recipient_notice_operator'));
CREATE POLICY recipient_notice_outbox ON public."OutboxMessages" FOR SELECT TO nexoruta_job_fn USING("Id"=nexoruta_app.ctx_uuid('recipient_notice_id') AND "Tipo"='AvisoDestinatarioEstado' AND "VersionContrato"=1 AND "Destino"='RabbitMq' AND "Estado" IN ('Procesando','Publicado'));
CREATE FUNCTION nexoruta_app.prepare_recipient_state_email(p_id uuid) RETURNS text
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE notice public."AvisosDestinatarioEstado"; result jsonb;
BEGIN
 PERFORM set_config('nexoruta.recipient_notice_id',p_id::text,true);
 IF NOT EXISTS(SELECT 1 FROM public."OutboxMessages" WHERE "Id"=p_id AND "Tipo"='AvisoDestinatarioEstado' AND "Estado" IN ('Procesando','Publicado')) THEN RETURN NULL; END IF;
 SELECT * INTO notice FROM public."AvisosDestinatarioEstado" WHERE "Id"=p_id FOR UPDATE;
 IF NOT FOUND OR notice."EnviadoEnUtc" IS NOT NULL THEN RETURN NULL; END IF;
 PERFORM set_config('nexoruta.recipient_notice_shipment',notice."EnvioId"::text,true);
 PERFORM set_config('nexoruta.recipient_notice_operator',notice."OperadorId"::text,true);
 SELECT jsonb_build_object('id',notice."Id",'operadorId',notice."OperadorId",'envioId',notice."EnvioId",'estado',notice."Estado",'destinatario',d."Email",'tokenHash',delivery."TokenHash",'tokenCifrado',encode(delivery."TokenCifrado",'base64'),'nonce',encode(delivery."Nonce",'base64'),'tag',encode(delivery."Tag",'base64'),
 'identidad',jsonb_build_object('nombre',coalesce(brand."NombreComercial",o."Nombre"),'correoPublico',brand."CorreoPublico",'telefonoPublico',brand."TelefonoPublico",'colorPrimario',brand."ColorPrimario",'colorSecundario',brand."ColorSecundario",'logoContenido',encode(brand."LogoContenido",'base64'),'logoTipoContenido',brand."LogoTipoContenido")) INTO result
 FROM public."Envios" e JOIN public."Destinatarios" d ON d."Id"=e."DestinatarioId" AND d."OperadorId"=e."OperadorId"
 JOIN public."Operadores" o ON o."Id"=e."OperadorId"
 LEFT JOIN LATERAL(SELECT * FROM public."VersionesIdentidadVisualOperador" b WHERE b."OperadorId"=e."OperadorId" ORDER BY b."Version" DESC LIMIT 1)brand ON true
 LEFT JOIN public."EntregasSeguimiento" delivery ON delivery."OperadorId"=e."OperadorId" AND delivery."EnvioId"=e."Id"
 AND EXISTS(SELECT 1 FROM public."EnlacesSeguimiento" l WHERE l."OperadorId"=e."OperadorId" AND l."EnvioId"=e."Id" AND l."TokenHash"=delivery."TokenHash" AND l."RevocadoEnUtc" IS NULL AND l."ExpiraEnUtc">clock_timestamp())
 WHERE e."Id"=notice."EnvioId" AND e."OperadorId"=notice."OperadorId";
 RETURN result::text;
END $$;
CREATE FUNCTION nexoruta_app.mark_recipient_state_email(p_id uuid) RETURNS boolean
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM set_config('nexoruta.recipient_notice_id',p_id::text,true);
 IF NOT EXISTS(SELECT 1 FROM public."OutboxMessages" WHERE "Id"=p_id AND "Tipo"='AvisoDestinatarioEstado' AND "Estado" IN ('Procesando','Publicado')) THEN RETURN false; END IF;
 UPDATE public."AvisosDestinatarioEstado" SET "EnviadoEnUtc"=coalesce("EnviadoEnUtc",clock_timestamp()) WHERE "Id"=p_id;
 RETURN FOUND;
END $$;
ALTER FUNCTION nexoruta_app.prepare_recipient_state_email(uuid) OWNER TO nexoruta_job_fn;
ALTER FUNCTION nexoruta_app.mark_recipient_state_email(uuid) OWNER TO nexoruta_job_fn;
REVOKE ALL ON FUNCTION nexoruta_app.prepare_recipient_state_email(uuid),nexoruta_app.mark_recipient_state_email(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nexoruta_app.prepare_recipient_state_email(uuid),nexoruta_app.mark_recipient_state_email(uuid) TO nexoruta_worker;
""");
    protected override void Down(MigrationBuilder m) => throw new NotSupportedException("Recipient notification history must be preserved.");
}
