using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NexoRuta.Infrastructure.Persistence.Migrations;

[DbContext(typeof(NexoRutaDbContext))]
[Migration("20261106190000_OperationalEventStream")]
public sealed class OperationalEventStream : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
-- Extend registered contracts without rewriting any previously published payload.
DO $m$ DECLARE definition text; BEGIN
 SELECT pg_get_constraintdef(oid) INTO definition FROM pg_constraint WHERE conrelid='public."OutboxMessages"'::regclass AND conname='CK_OutboxMessages_Contract';
 EXECUTE 'ALTER TABLE public."OutboxMessages" DROP CONSTRAINT "CK_OutboxMessages_Contract"';
 EXECUTE 'ALTER TABLE public."OutboxMessages" ADD CONSTRAINT "CK_OutboxMessages_Contract" CHECK (('
 || substring(definition from 8 for length(definition)-8)
 || ') OR ("Tipo"=''EventoOperativo'' AND "VersionContrato"=1 AND "Destino"=''Kafka'' AND "Ambito"=''Flota'' AND "EstadoEntrega" IS NULL))';
END $m$;
ALTER TABLE public."OutboxMessages" DROP CONSTRAINT "CK_OutboxMessages_Scope";
ALTER TABLE public."OutboxMessages" ADD CONSTRAINT "CK_OutboxMessages_Scope" CHECK (
 ("Ambito"='Envio' AND "OperadorId" IS NOT NULL AND "EnvioId" IS NOT NULL AND "ClaveAmbito"="EnvioId") OR
 ("Ambito"='Identidad' AND "OperadorId" IS NULL AND "EnvioId" IS NULL) OR
 ("Ambito"='Flota' AND "OperadorId" IS NOT NULL AND "EnvioId" IS NULL));
CREATE POLICY history_operational_outbox ON public."OutboxMessages" FOR INSERT TO nexoruta_history_fn
 WITH CHECK("Tipo"='EventoOperativo' AND "VersionContrato"=1 AND "Destino"='Kafka' AND "Ambito"='Flota');

GRANT SELECT ON public."VersionesReglasOperativas",public."TarifasEnvio",public."Repartidores",public."Vehiculos" TO nexoruta_history_fn;
CREATE POLICY operational_courier_source ON public."Repartidores" FOR SELECT TO nexoruta_history_fn USING(true);
CREATE POLICY operational_vehicle_source ON public."Vehiculos" FOR SELECT TO nexoruta_history_fn USING(true);
CREATE POLICY operational_rules_source ON public."VersionesReglasOperativas" FOR SELECT TO nexoruta_history_fn USING(true);
CREATE POLICY operational_tariff_source ON public."TarifasEnvio" FOR SELECT TO nexoruta_history_fn USING(true);
CREATE FUNCTION public.operational_shipment_data(p_operator uuid,p_shipment uuid) RETURNS jsonb
 LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $$
 SELECT jsonb_build_object('estado',e."Estado",'esRetiro',e."EsRetiro",'modalidad',t."Servicio"->>'modalidad',
 'compromisoHastaUtc',CASE t."Servicio"->>'modalidad'
 WHEN 'Estandar' THEN e."CreadoEnUtc"+make_interval(mins=>r."PlazoEstandarMinutos")
 WHEN 'Urgente' THEN e."CreadoEnUtc"+make_interval(mins=>r."PlazoUrgenteMinutos") END,
 'compromisoOrigen','ReglasCapturadasDesdeAdmision','versionReglasId',e."VersionReglasId")
 FROM public."Envios" e JOIN public."VersionesReglasOperativas" r ON r."OperadorId"=e."OperadorId" AND r."Id"=e."VersionReglasId"
 LEFT JOIN LATERAL(SELECT "Servicio" FROM public."TarifasEnvio" WHERE "OperadorId"=e."OperadorId" AND "EnvioId"=e."Id" ORDER BY "CreadoEnUtc","Id" LIMIT 1)t ON true
 WHERE e."OperadorId"=p_operator AND e."Id"=p_shipment
 $$;
ALTER FUNCTION public.operational_shipment_data(uuid,uuid) OWNER TO nexoruta_history_fn;
REVOKE ALL ON FUNCTION public.operational_shipment_data(uuid,uuid) FROM PUBLIC;

CREATE POLICY operational_outbox_source_lookup ON public."OutboxMessages" FOR SELECT TO nexoruta_history_fn
 USING("Tipo"='EventoOperativo' AND "VersionContrato"=1 AND "Destino"='Kafka' AND "Ambito"='Flota');

-- Every event is captured in the same PostgreSQL transaction as the accepted fact.
-- Explicit field allowlists keep personal data and evidence out of this stream.
CREATE FUNCTION public.capture_operational_event() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE j jsonb:=to_jsonb(NEW); prev jsonb; event_id uuid:=gen_random_uuid(); operator_id uuid:=(j->>'OperadorId')::uuid;
 entity uuid; shipment uuid; route uuid; commerce uuid; kind text; version bigint; occurred timestamptz; recorded timestamptz:=clock_timestamp(); data jsonb; correlation uuid;
BEGIN
 IF TG_OP='UPDATE' THEN prev:=to_jsonb(OLD); IF prev=j THEN RETURN NEW; END IF; END IF;
 CASE TG_TABLE_NAME
 WHEN 'EnvioEventos' THEN
  kind:='Envio'; entity:=(j->>'EnvioId')::uuid; shipment:=entity; commerce:=(j->>'ComercioId')::uuid;
  version:=(j->>'VersionNueva')::bigint; event_id:=(j->>'Id')::uuid;
  occurred:=(j->>'OcurridaEnUtc')::timestamptz; recorded:=(j->>'RegistradaEnUtc')::timestamptz;
  data:=public.operational_shipment_data(operator_id,shipment)||jsonb_build_object('estado',coalesce(j->>'EstadoNuevo',j->>'EstadoObservado'),'hecho',j->>'CodigoHecho','origen',j->>'OrigenTipo');
 WHEN 'TarifasEnvio' THEN
  kind:='Envio'; shipment:=(j->>'EnvioId')::uuid; entity:=shipment;
  SELECT e."ComercioId",e."Version" INTO commerce,version FROM public."Envios" e WHERE e."OperadorId"=operator_id AND e."Id"=shipment;
  data:=public.operational_shipment_data(operator_id,shipment);
 WHEN 'Bultos' THEN
  IF TG_OP='UPDATE' AND j->>'Version'=prev->>'Version' THEN RETURN NEW; END IF;
  kind:='Bulto'; entity:=(j->>'Id')::uuid; shipment:=(j->>'EnvioId')::uuid; version:=(j->>'Version')::bigint;
  SELECT "ComercioId" INTO commerce FROM public."Envios" WHERE "OperadorId"=operator_id AND "Id"=shipment;
  occurred:=nullif(j->>'EntregadoEnUtc','')::timestamptz;
  data:=jsonb_build_object('resultado',j->>'Resultado','custodia',j->>'Custodia');
 WHEN 'OperacionesJornada' THEN
  IF j->>'Resultado'<>'Aplicada' OR j->>'Tipo'<>'Posicion' THEN RETURN NEW; END IF;
  kind:='Posicion'; entity:=(j->>'RutaId')::uuid; route:=entity; version:=(j->>'Secuencia')::bigint;
  data:=j->'Payload'->'datos'; occurred:=(data->>'posicionEnUtc')::timestamptz;
  IF occurred IS NULL THEN RETURN NEW; END IF;
  data:=jsonb_build_object('latitud',data->'latitud','longitud',data->'longitud','precisionMetros',data->'precisionMetros','repartidorAccesoId',j->'AccesoId');
  recorded:=(j->>'RecibidaEnUtc')::timestamptz;
 WHEN 'HojasRuta' THEN
  kind:='Ruta'; entity:=(j->>'Id')::uuid; route:=entity; version:=(j->>'Version')::bigint;
  data:=jsonb_build_object('estado',j->>'Estado','vehiculoId',j->'VehiculoId','repartidorAccesoId',j->'RepartidorAccesoId','fecha',j->'Fecha','repartidorNombre',(SELECT r."Nombre" FROM public."Repartidores" r WHERE r."OperadorId"=operator_id AND r."AccesoId"=(j->>'RepartidorAccesoId')::uuid),'matricula',(SELECT v."Matricula" FROM public."Vehiculos" v WHERE v."OperadorId"=operator_id AND v."Id"=(j->>'VehiculoId')::uuid));
 WHEN 'ParadasRuta' THEN
  kind:='Parada'; route:=(j->>'RutaId')::uuid; shipment:=(j->>'EnvioId')::uuid; entity:=md5(route::text || ':' || shipment::text)::uuid;
  version:=0; -- receipt order is not used as a domain version; this type has its own event timestamp.
  SELECT "ComercioId" INTO commerce FROM public."Envios" WHERE "OperadorId"=operator_id AND "Id"=shipment;
  data:=jsonb_build_object('orden',j->'Orden','activa',j->'Activa','resultadoVisita',j->'ResultadoVisita','bultoIds',j->'BultoIds','llegadaEstimadaUtc',j->'LlegadaEstimadaUtc','servicioMinutos',j->'ServicioMinutos');
 WHEN 'EstimacionesRuta' THEN
  kind:='Estimacion'; entity:=(j->>'RutaId')::uuid; route:=entity; version:=(j->>'NumeroVersion')::bigint;
  recorded:=(j->>'RegistradaEnUtc')::timestamptz;
  data:=jsonb_build_object('resultado',j->'Resultado','motivo',j->>'Motivo','versionRuta',j->'VersionRuta');
 WHEN 'HechosFlota' THEN
  kind:='Flota'; entity:=(j->>'VehiculoId')::uuid; route:=(j->>'RutaId')::uuid; version:=(j->>'VersionAnterior')::bigint+1;
  occurred:=(j->>'ConfirmadoEnUtc')::timestamptz; data:=jsonb_build_object('accion',j->>'Accion');
 ELSE RAISE EXCEPTION 'Unregistered operational source';
 END CASE;
 IF nexoruta_app.setting('operational_bootstrap')='v1' THEN data:=data||jsonb_build_object('procedencia','SnapshotBootstrap'); END IF;
 correlation:=coalesce((j->>'CorrelacionId')::uuid,(j->>'ComandoId')::uuid,CASE WHEN TG_TABLE_NAME='OperacionesJornada' THEN (j->>'Id')::uuid END,nullif(nexoruta_app.setting('history_correlation_id'),'')::uuid,event_id);
 INSERT INTO public."OutboxMessages"("Id","OperadorId","EnvioId","Ambito","ClaveAmbito","Tipo","VersionContrato","Destino","Prioridad","Estado","Payload","Intentos","CreadoEnUtc","ProximoIntentoEnUtc")
 VALUES(event_id,operator_id,NULL,'Flota',entity,'EventoOperativo',1,'Kafka','Normal','Pendiente',
 jsonb_build_object('schemaVersion',1,'eventId',event_id,'operadorId',operator_id,'comercioId',commerce,'tipo',kind,'entidadId',entity,'envioId',shipment,'rutaId',route,'version',version,'correlacionId',correlation,'ocurridaEnUtc',occurred,'registradaEnUtc',recorded,'datos',data),0,recorded,recorded);
 RETURN NEW;
END $$;
ALTER FUNCTION public.capture_operational_event() OWNER TO nexoruta_history_fn;
REVOKE ALL ON FUNCTION public.capture_operational_event() FROM PUBLIC;
CREATE TRIGGER operational_tariff AFTER INSERT ON public."TarifasEnvio" FOR EACH ROW EXECUTE FUNCTION public.capture_operational_event();
CREATE TRIGGER operational_history AFTER INSERT ON public."EnvioEventos" FOR EACH ROW EXECUTE FUNCTION public.capture_operational_event();
CREATE TRIGGER operational_parcel AFTER INSERT OR UPDATE ON public."Bultos" FOR EACH ROW EXECUTE FUNCTION public.capture_operational_event();
CREATE TRIGGER operational_position AFTER INSERT ON public."OperacionesJornada" FOR EACH ROW EXECUTE FUNCTION public.capture_operational_event();
CREATE TRIGGER operational_route AFTER INSERT OR UPDATE ON public."HojasRuta" FOR EACH ROW EXECUTE FUNCTION public.capture_operational_event();
CREATE TRIGGER operational_stop AFTER INSERT OR UPDATE ON public."ParadasRuta" FOR EACH ROW EXECUTE FUNCTION public.capture_operational_event();
CREATE TRIGGER operational_estimate AFTER INSERT ON public."EstimacionesRuta" FOR EACH ROW EXECUTE FUNCTION public.capture_operational_event();
CREATE TRIGGER operational_fleet AFTER INSERT ON public."HechosFlota" FOR EACH ROW EXECUTE FUNCTION public.capture_operational_event();
""");
    protected override void Down(MigrationBuilder m) => throw new NotSupportedException("Operational log captures accepted facts and must not be rolled back destructively.");
}
