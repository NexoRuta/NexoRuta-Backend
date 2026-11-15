using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace NexoRuta.Infrastructure.Persistence.Migrations;

[DbContext(typeof(NexoRutaDbContext))]
[Migration("20261115210000_AdministrativeOutboxRetry")]
public sealed class AdministrativeOutboxRetry : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
DO $$ BEGIN
 IF NOT EXISTS(SELECT FROM pg_roles WHERE rolname='nexoruta_retry_fn') THEN
 CREATE ROLE nexoruta_retry_fn NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
 END IF;
END $$;
GRANT USAGE ON SCHEMA public,nexoruta_app TO nexoruta_retry_fn;
GRANT EXECUTE ON FUNCTION nexoruta_app.setting(text),nexoruta_app.ctx_uuid(text) TO nexoruta_retry_fn;
GRANT SELECT ON public."OutboxMessages",public."ProcessedMessages",public."AccesosUsuario",public."RolesPermisos",public."Permisos",public."Operadores" TO nexoruta_retry_fn;
GRANT UPDATE("Estado","ProximoIntentoEnUtc") ON public."OutboxMessages" TO nexoruta_retry_fn;
CREATE POLICY retry_outbox_read ON public."OutboxMessages" FOR SELECT TO nexoruta_retry_fn
 USING(nexoruta_app.setting('actor_kind')='operador' AND "OperadorId"=nexoruta_app.ctx_uuid('org_id'));
CREATE POLICY retry_outbox_update ON public."OutboxMessages" FOR UPDATE TO nexoruta_retry_fn
 USING(nexoruta_app.setting('actor_kind')='operador' AND "OperadorId"=nexoruta_app.ctx_uuid('org_id') )
 WITH CHECK("OperadorId"=nexoruta_app.ctx_uuid('org_id') AND "Estado"='Pendiente');
CREATE POLICY retry_processed_read ON public."ProcessedMessages" FOR SELECT TO nexoruta_retry_fn
 USING(EXISTS(SELECT 1 FROM public."OutboxMessages" m WHERE m."Id"="MessageId"));
CREATE POLICY retry_access ON public."AccesosUsuario" FOR SELECT TO nexoruta_retry_fn USING("Id"=nexoruta_app.ctx_uuid('access_id'));
CREATE POLICY retry_grants ON public."RolesPermisos" FOR SELECT TO nexoruta_retry_fn USING("RolId"=nexoruta_app.ctx_uuid('retry_role_id'));
CREATE POLICY retry_permissions ON public."Permisos" FOR SELECT TO nexoruta_retry_fn USING(true);
CREATE POLICY retry_operator ON public."Operadores" FOR SELECT TO nexoruta_retry_fn USING("Id"=nexoruta_app.ctx_uuid('org_id'));

CREATE TABLE public."OutboxRetryRequests"(
 "Id" uuid NOT NULL,"OperadorId" uuid NOT NULL,"ActorId" uuid NOT NULL,"AccesoId" uuid NOT NULL,
 "MensajeId" uuid NOT NULL,"IntentosEsperados" integer NOT NULL,"Motivo" varchar(500) NOT NULL,
 "FechaUtc" timestamptz NOT NULL DEFAULT clock_timestamp(),"Resultado" text NOT NULL,
 "EstadoAnterior" text,"Diagnostico" text,PRIMARY KEY("OperadorId","Id"),
 CHECK("Resultado" IN('Solicitado','NoElegible','NoDisponible')));
ALTER TABLE public."OutboxRetryRequests" OWNER TO nexoruta_schema_owner;
ALTER TABLE public."OutboxRetryRequests" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."OutboxRetryRequests" FORCE ROW LEVEL SECURITY;
CREATE POLICY retry_requests_fn ON public."OutboxRetryRequests" TO nexoruta_retry_fn
 USING("OperadorId"=nexoruta_app.ctx_uuid('org_id')) WITH CHECK("OperadorId"=nexoruta_app.ctx_uuid('org_id'));
GRANT SELECT,INSERT ON public."OutboxRetryRequests" TO nexoruta_retry_fn;
CREATE FUNCTION public.guard_outbox_retry_history() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN RAISE EXCEPTION 'Outbox retry history is immutable' USING ERRCODE='55000'; END $$;
CREATE TRIGGER retry_history_immutable BEFORE UPDATE OR DELETE ON public."OutboxRetryRequests" FOR EACH ROW EXECUTE FUNCTION public.guard_outbox_retry_history();
CREATE TRIGGER retry_history_no_truncate BEFORE TRUNCATE ON public."OutboxRetryRequests" FOR EACH STATEMENT EXECUTE FUNCTION public.guard_outbox_retry_history();

GRANT CREATE ON SCHEMA nexoruta_app TO nexoruta_retry_fn;
CREATE FUNCTION nexoruta_app.require_outbox_manager() RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE role_id uuid;
BEGIN
 IF nexoruta_app.setting('actor_kind') IS DISTINCT FROM 'operador' THEN RAISE EXCEPTION 'Operator required' USING ERRCODE='42501'; END IF;
 SELECT a."RolId" INTO role_id FROM public."AccesosUsuario" a JOIN public."Operadores" o ON o."Id"=a."OrganizacionId"
 WHERE a."Id"=nexoruta_app.ctx_uuid('access_id') AND a."UsuarioId"=nexoruta_app.ctx_uuid('user_id')
 AND a."OrganizacionId"=nexoruta_app.ctx_uuid('org_id') AND a."TipoOrganizacion"='Operador' AND a."Activo" AND o."Estado"='Activa';
 PERFORM set_config('nexoruta.retry_role_id',role_id::text,true);
 IF role_id IS NULL OR (SELECT count(DISTINCT p."Codigo") FROM public."RolesPermisos" rp JOIN public."Permisos" p ON p."Id"=rp."PermisoId"
 WHERE rp."RolId"=role_id AND p."Codigo" IN('configuracion.gestionar','envios.consultar'))<>2 THEN
 RAISE EXCEPTION 'Outbox permission required' USING ERRCODE='42501'; END IF;
END $$;
ALTER FUNCTION nexoruta_app.require_outbox_manager() OWNER TO nexoruta_retry_fn;
REVOKE ALL ON FUNCTION nexoruta_app.require_outbox_manager() FROM PUBLIC;

CREATE FUNCTION nexoruta_app.failed_outbox_messages(p_page int,p_size int)
 RETURNS TABLE("Id" uuid,"Tipo" text,"Destino" text,"Estado" text,"Intentos" int,"Diagnostico" text,"ProximoIntentoEnUtc" timestamptz)
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 PERFORM nexoruta_app.require_outbox_manager();
 IF p_page NOT BETWEEN 1 AND 100000 OR p_size NOT BETWEEN 1 AND 100 THEN RAISE EXCEPTION 'Invalid pagination'; END IF;
 RETURN QUERY SELECT m."Id",m."Tipo"::text,m."Destino"::text,m."Estado"::text,m."Intentos",
 'Falló la publicación. El Worker conserva el reintento automático.'::text,m."ProximoIntentoEnUtc"
 FROM public."OutboxMessages" m WHERE m."Estado"='Error' AND m."Destino" IN('RabbitMq','Kafka')
 AND m."EstadoEntrega" IS NULL AND m."ProcesadorId" IS NULL AND m."ReservaHastaUtc" IS NULL
 AND NOT EXISTS(SELECT 1 FROM public."ProcessedMessages" p WHERE p."MessageId"=m."Id")
 ORDER BY m."CreadoEnUtc",m."Id" OFFSET (p_page-1)*p_size LIMIT p_size;
END $$;
ALTER FUNCTION nexoruta_app.failed_outbox_messages(int,int) OWNER TO nexoruta_retry_fn;
REVOKE ALL ON FUNCTION nexoruta_app.failed_outbox_messages(int,int) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nexoruta_app.failed_outbox_messages(int,int) TO nexoruta_api;

CREATE FUNCTION nexoruta_app.request_outbox_retry(p_request uuid,p_message uuid,p_attempts int,p_reason text) RETURNS text
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE m public."OutboxMessages"; previous public."OutboxRetryRequests"; result text;
BEGIN
 PERFORM nexoruta_app.require_outbox_manager();
 IF p_request IS NULL OR p_request='00000000-0000-0000-0000-000000000000'::uuid OR p_message IS NULL OR p_attempts<0
 OR p_reason IS NULL OR length(btrim(p_reason)) NOT BETWEEN 1 AND 500 THEN RAISE EXCEPTION 'Invalid retry request' USING ERRCODE='22023'; END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended(p_request::text,21));
 SELECT * INTO previous FROM public."OutboxRetryRequests" WHERE "Id"=p_request;
 IF FOUND THEN
 IF previous."MensajeId"<>p_message OR previous."AccesoId"<>nexoruta_app.ctx_uuid('access_id') OR previous."IntentosEsperados"<>p_attempts OR previous."Motivo"<>btrim(p_reason) THEN
 RAISE EXCEPTION 'Retry identity reused with different content' USING ERRCODE='22023'; END IF;
 RETURN previous."Resultado";
 END IF;
 SELECT * INTO m FROM public."OutboxMessages" WHERE "Id"=p_message FOR UPDATE;
 IF NOT FOUND THEN result:='NoDisponible';
 ELSE
 PERFORM set_config('nexoruta.retry_message_id',p_message::text,true);
 IF m."Estado"='Error' AND m."Intentos"=p_attempts AND m."Destino" IN('RabbitMq','Kafka')
 AND m."ProcesadorId" IS NULL AND m."ReservaHastaUtc" IS NULL AND m."EstadoEntrega" IS NULL
 AND NOT EXISTS(SELECT 1 FROM public."ProcessedMessages" WHERE "MessageId"=p_message) THEN
 UPDATE public."OutboxMessages" SET "Estado"='Pendiente',"ProximoIntentoEnUtc"=clock_timestamp() WHERE "Id"=p_message;
 result:='Solicitado';
 ELSE result:='NoElegible'; END IF;
 END IF;
 INSERT INTO public."OutboxRetryRequests"("Id","OperadorId","ActorId","AccesoId","MensajeId","IntentosEsperados","Motivo","Resultado","EstadoAnterior","Diagnostico")
 VALUES(p_request,nexoruta_app.ctx_uuid('org_id'),nexoruta_app.ctx_uuid('user_id'),nexoruta_app.ctx_uuid('access_id'),p_message,p_attempts,btrim(p_reason),result,m."Estado",'Solicitud de reintento de publicación');
 RETURN result;
END $$;
ALTER FUNCTION nexoruta_app.request_outbox_retry(uuid,uuid,int,text) OWNER TO nexoruta_retry_fn;
REVOKE ALL ON FUNCTION nexoruta_app.request_outbox_retry(uuid,uuid,int,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nexoruta_app.request_outbox_retry(uuid,uuid,int,text) TO nexoruta_api;
REVOKE CREATE ON SCHEMA nexoruta_app FROM nexoruta_retry_fn;
""");
    protected override void Down(MigrationBuilder m) => throw new NotSupportedException("Retry audit history must be retained.");
}
