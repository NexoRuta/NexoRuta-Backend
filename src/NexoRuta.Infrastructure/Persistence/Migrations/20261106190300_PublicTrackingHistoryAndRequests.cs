using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace NexoRuta.Infrastructure.Persistence.Migrations;

[DbContext(typeof(NexoRutaDbContext))]
[Migration("20261106190300_PublicTrackingHistoryAndRequests")]
public sealed class PublicTrackingHistoryAndRequests : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
GRANT SELECT ON public."EnvioEventos",public."VersionesReglasOperativas",public."OperacionesJornada" TO nexoruta_public_fn;
CREATE POLICY tracking_history ON public."EnvioEventos" FOR SELECT TO nexoruta_public_fn USING(
 EXISTS(SELECT 1 FROM public."Envios" e WHERE e."OperadorId"="EnvioEventos"."OperadorId" AND e."Id"="EnvioEventos"."EnvioId"));
CREATE POLICY tracking_rules ON public."VersionesReglasOperativas" FOR SELECT TO nexoruta_public_fn USING(
 EXISTS(SELECT 1 FROM public."Envios" e WHERE e."OperadorId"="VersionesReglasOperativas"."OperadorId" AND e."VersionReglasId"="VersionesReglasOperativas"."Id"));
CREATE POLICY tracking_attempts ON public."OperacionesJornada" FOR SELECT TO nexoruta_public_fn USING(
 "Tipo"='IntentoFallido' AND "Resultado"='Aplicada' AND EXISTS(SELECT 1 FROM public."Envios" e WHERE e."OperadorId"="OperacionesJornada"."OperadorId" AND e."Id"="OperacionesJornada"."EnvioId"));
CREATE FUNCTION nexoruta_app.public_tracking_history(p_token text) RETURNS TABLE("Estado" text,"OcurridaEnUtc" timestamptz,"RegistradaEnUtc" timestamptz)
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF p_token IS NULL OR p_token !~ '^[0-9a-f]{64}$' THEN RETURN; END IF;
 PERFORM set_config('nexoruta.public_token_hash',encode(public.digest(convert_to(p_token,'SQL_ASCII'),'sha256'),'hex'),true);
 RETURN QUERY SELECT h."EstadoNuevo"::text,h."OcurridaEnUtc",h."RegistradaEnUtc" FROM public."EnvioEventos" h
 WHERE h."Tipo" IN ('Creacion','TransicionEstado') AND h."EstadoNuevo" IN ('Admitido','EnDeposito','AsignadoRuta','EnTransito','Entregado','EntregaPendienteValidacion','EntregaSinValidacion','EntregadoParcialmente','NoEntregado','Reprogramado','EnDevolucion','Devuelto','Extraviado','Cancelado','RetiroSolicitado','RetiroAsignado','Retirado','RetiroFallido','RetiroEnDeposito','EntregadoComercio')
 ORDER BY h."VersionNueva" DESC,h."RegistradaEnUtc" DESC,h."Id" DESC LIMIT 100;
END $$;
ALTER FUNCTION nexoruta_app.public_tracking_history(text) OWNER TO nexoruta_public_fn;
REVOKE ALL ON FUNCTION nexoruta_app.public_tracking_history(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nexoruta_app.public_tracking_history(text) TO nexoruta_api;

CREATE TABLE public."SolicitudesReprogramacionPublica"("Id" uuid PRIMARY KEY,"OperadorId" uuid NOT NULL,"ComercioId" uuid NOT NULL,"EnvioId" uuid NOT NULL,"EnlaceId" uuid NOT NULL,"VersionEnvio" bigint NOT NULL,"CreadaEnUtc" timestamptz NOT NULL DEFAULT clock_timestamp(),"Estado" text NOT NULL CHECK("Estado" IN ('Pendiente','Tratada','Rechazada')),"TratadaEnUtc" timestamptz,"TratadaPorAccesoId" uuid,"Motivo" text,
 FOREIGN KEY("OperadorId","EnvioId") REFERENCES public."Envios"("OperadorId","Id"), FOREIGN KEY("ComercioId","EnvioId") REFERENCES public."Envios"("ComercioId","Id"));
CREATE UNIQUE INDEX ON public."SolicitudesReprogramacionPublica"("OperadorId","EnvioId") WHERE "Estado"='Pendiente';
CREATE INDEX ON public."SolicitudesReprogramacionPublica"("OperadorId","Estado","CreadaEnUtc","Id");
ALTER TABLE public."SolicitudesReprogramacionPublica" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."SolicitudesReprogramacionPublica" FORCE ROW LEVEL SECURITY;
GRANT SELECT,INSERT ON public."SolicitudesReprogramacionPublica" TO nexoruta_public_fn;
CREATE POLICY public_request_token ON public."SolicitudesReprogramacionPublica" TO nexoruta_public_fn USING(
 EXISTS(SELECT 1 FROM public."EnlacesSeguimiento" l WHERE l."Id"="SolicitudesReprogramacionPublica"."EnlaceId" AND l."OperadorId"="SolicitudesReprogramacionPublica"."OperadorId" AND l."EnvioId"="SolicitudesReprogramacionPublica"."EnvioId")) WITH CHECK(
 EXISTS(SELECT 1 FROM public."EnlacesSeguimiento" l WHERE l."Id"="SolicitudesReprogramacionPublica"."EnlaceId" AND l."OperadorId"="SolicitudesReprogramacionPublica"."OperadorId" AND l."EnvioId"="SolicitudesReprogramacionPublica"."EnvioId"));
GRANT SELECT,UPDATE("Estado","TratadaEnUtc","TratadaPorAccesoId","Motivo") ON public."SolicitudesReprogramacionPublica" TO nexoruta_api;
CREATE POLICY operator_request_scope ON public."SolicitudesReprogramacionPublica" TO nexoruta_api USING(
 nexoruta_app.setting('actor_kind')='operador' AND "OperadorId"=nexoruta_app.ctx_uuid('org_id')) WITH CHECK(
 nexoruta_app.setting('actor_kind')='operador' AND "OperadorId"=nexoruta_app.ctx_uuid('org_id'));
CREATE FUNCTION nexoruta_app.request_public_reschedule(p_token text,p_request uuid) RETURNS text
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE e public."Envios"; l public."EnlacesSeguimiento"; r public."VersionesReglasOperativas"; attempts integer; existing public."SolicitudesReprogramacionPublica";
BEGIN
 IF p_token IS NULL OR p_token !~ '^[0-9a-f]{64}$' OR p_request IS NULL OR p_request='00000000-0000-0000-0000-000000000000'::uuid THEN RETURN 'NoDisponible'; END IF;
 PERFORM set_config('nexoruta.public_token_hash',encode(public.digest(convert_to(p_token,'SQL_ASCII'),'sha256'),'hex'),true);
 SELECT * INTO l FROM public."EnlacesSeguimiento" LIMIT 1; IF NOT FOUND THEN RETURN 'NoDisponible'; END IF;
 SELECT * INTO e FROM public."Envios" WHERE "OperadorId"=l."OperadorId" AND "Id"=l."EnvioId";
 IF NOT FOUND THEN RETURN 'NoDisponible'; END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended(e."Id"::text,0));
 SELECT * INTO existing FROM public."SolicitudesReprogramacionPublica" WHERE "Id"=p_request;
 IF FOUND THEN RETURN existing."Estado"; END IF;
 IF e."Estado"<>'NoEntregado' OR e."EsRetiro" OR e."EntregaFisicaEnUtc" IS NOT NULL THEN RETURN 'NoElegible'; END IF;
 SELECT * INTO r FROM public."VersionesReglasOperativas" WHERE "OperadorId"=e."OperadorId" AND "Id"=e."VersionReglasId";
 SELECT count(*)::integer INTO attempts FROM public."OperacionesJornada" WHERE "OperadorId"=e."OperadorId" AND "EnvioId"=e."Id" AND "Tipo"='IntentoFallido' AND "Resultado"='Aplicada';
 IF r."MaxIntentosEntrega" IS NULL OR r."IntervaloEntreIntentosMinutos" IS NULL OR attempts=0 OR attempts>=r."MaxIntentosEntrega" THEN RETURN 'NoElegible'; END IF;
 INSERT INTO public."SolicitudesReprogramacionPublica"("Id","OperadorId","ComercioId","EnvioId","EnlaceId","VersionEnvio","Estado") VALUES(p_request,e."OperadorId",e."ComercioId",e."Id",l."Id",e."Version",'Pendiente') ON CONFLICT DO NOTHING;
 IF NOT FOUND AND NOT EXISTS(SELECT 1 FROM public."SolicitudesReprogramacionPublica" WHERE "EnvioId"=e."Id" AND "OperadorId"=e."OperadorId" AND "Estado"='Pendiente') THEN RETURN 'NoDisponible'; END IF;
 RETURN 'Pendiente';
END $$;
ALTER FUNCTION nexoruta_app.request_public_reschedule(text,uuid) OWNER TO nexoruta_public_fn;
REVOKE ALL ON FUNCTION nexoruta_app.request_public_reschedule(text,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nexoruta_app.request_public_reschedule(text,uuid) TO nexoruta_api;
GRANT SELECT ON public."ParadasRuta",public."HojasRuta" TO nexoruta_public_fn;
CREATE POLICY tracking_stop ON public."ParadasRuta" FOR SELECT TO nexoruta_public_fn USING(
 EXISTS(SELECT 1 FROM public."Envios" e WHERE e."OperadorId"="ParadasRuta"."OperadorId" AND e."Id"="ParadasRuta"."EnvioId"));
CREATE POLICY tracking_route ON public."HojasRuta" FOR SELECT TO nexoruta_public_fn USING(
 EXISTS(SELECT 1 FROM public."ParadasRuta" p WHERE p."OperadorId"="HojasRuta"."OperadorId" AND p."RutaId"="HojasRuta"."Id"));
CREATE FUNCTION nexoruta_app.public_parcel_details(p_token text) RETURNS TABLE("Numero" integer,"Resultado" text)
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF p_token IS NULL OR p_token !~ '^[0-9a-f]{64}$' THEN RETURN; END IF;
 PERFORM set_config('nexoruta.public_token_hash',encode(public.digest(convert_to(p_token,'SQL_ASCII'),'sha256'),'hex'),true);
 RETURN QUERY SELECT row_number() OVER(ORDER BY b."Id")::integer,b."Resultado"::text FROM public."Bultos" b ORDER BY b."Id" LIMIT 200;
END $$;
ALTER FUNCTION nexoruta_app.public_parcel_details(text) OWNER TO nexoruta_public_fn;
REVOKE ALL ON FUNCTION nexoruta_app.public_parcel_details(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nexoruta_app.public_parcel_details(text) TO nexoruta_api;

ALTER TABLE public."SolicitudesReprogramacionPublica" OWNER TO nexoruta_schema_owner;
""");
    protected override void Down(MigrationBuilder m) => throw new NotSupportedException("Public requests must be preserved.");
}
