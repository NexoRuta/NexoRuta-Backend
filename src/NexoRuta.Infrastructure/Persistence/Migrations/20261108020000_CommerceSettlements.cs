using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace NexoRuta.Infrastructure.Persistence.Migrations;
[DbContext(typeof(NexoRutaDbContext))]
[Migration("20261108020000_CommerceSettlements")]
public sealed class CommerceSettlements : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
CREATE TABLE public."Liquidaciones"("Id" uuid PRIMARY KEY,"OperadorId" uuid NOT NULL,"ComercioId" uuid NOT NULL,"Estado" text NOT NULL CHECK("Estado" IN ('Borrador','Confirmada','Descartada')),"DesdeUtc" timestamptz NOT NULL,"HastaUtc" timestamptz NOT NULL,"CorteUtc" timestamptz NOT NULL,"TotalUyu" numeric(18,2) NOT NULL,"PreparadaPor" uuid NOT NULL,"PreparadaEnUtc" timestamptz NOT NULL DEFAULT clock_timestamp(),"ConfirmadaPor" uuid,"ConfirmadaEnUtc" timestamptz,CHECK("HastaUtc">"DesdeUtc"),UNIQUE("OperadorId","Id"));
CREATE TABLE public."LiquidacionLineas"("OperadorId" uuid NOT NULL,"LiquidacionId" uuid NOT NULL,"MovimientoId" uuid NOT NULL,"EnvioId" uuid NOT NULL,"FechaUtc" timestamptz NOT NULL,"Concepto" text NOT NULL,"ImporteUyu" numeric(14,2) NOT NULL,"Fuente" text NOT NULL,PRIMARY KEY("LiquidacionId","MovimientoId"),FOREIGN KEY("OperadorId","LiquidacionId") REFERENCES public."Liquidaciones"("OperadorId","Id"));
CREATE TABLE public."CargosLiquidados"("MovimientoId" uuid PRIMARY KEY,"OperadorId" uuid NOT NULL,"LiquidacionId" uuid NOT NULL,FOREIGN KEY("LiquidacionId","MovimientoId") REFERENCES public."LiquidacionLineas"("LiquidacionId","MovimientoId"));
CREATE TABLE public."LiquidacionTransiciones"("Id" uuid PRIMARY KEY DEFAULT gen_random_uuid(),"OperadorId" uuid NOT NULL,"LiquidacionId" uuid NOT NULL,"Estado" text NOT NULL,"ActorId" uuid NOT NULL,"FechaUtc" timestamptz NOT NULL DEFAULT clock_timestamp(),FOREIGN KEY("OperadorId","LiquidacionId") REFERENCES public."Liquidaciones"("OperadorId","Id"));
CREATE TABLE public."CorreccionesCuenta"("Id" uuid PRIMARY KEY,"OperadorId" uuid NOT NULL,"ComercioId" uuid NOT NULL,"EnvioId" uuid NOT NULL,"MovimientoOriginalId" uuid NOT NULL REFERENCES public."CargosLiquidados"("MovimientoId"),"ImporteUyu" numeric(14,2) NOT NULL CHECK("ImporteUyu"<>0),"Motivo" text NOT NULL CHECK(length(btrim("Motivo")) BETWEEN 10 AND 500),"ActorId" uuid NOT NULL,"FechaUtc" timestamptz NOT NULL DEFAULT clock_timestamp());
CREATE INDEX ON public."Liquidaciones"("OperadorId","ComercioId","PreparadaEnUtc","Id");
CREATE INDEX ON public."CorreccionesCuenta"("OperadorId","ComercioId","FechaUtc");
DO $m$ DECLARE t text; BEGIN
 FOREACH t IN ARRAY ARRAY['Liquidaciones','LiquidacionLineas','CargosLiquidados','LiquidacionTransiciones','CorreccionesCuenta'] LOOP
 EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY',t);
 EXECUTE format('ALTER TABLE public.%I FORCE ROW LEVEL SECURITY',t);
 EXECUTE format('GRANT SELECT ON public.%I TO nexoruta_api',t);
 EXECUTE format('GRANT SELECT,INSERT,UPDATE ON public.%I TO nexoruta_shipment_fn',t);
 EXECUTE format('CREATE POLICY settlement_function ON public.%I TO nexoruta_shipment_fn USING("OperadorId"=nexoruta_app.ctx_uuid(''org_id'') AND nexoruta_app.setting(''actor_kind'')=''operador'') WITH CHECK("OperadorId"=nexoruta_app.ctx_uuid(''org_id'') AND nexoruta_app.setting(''actor_kind'')=''operador'')',t);
 END LOOP;
END $m$;
CREATE POLICY settlement_read ON public."Liquidaciones" FOR SELECT TO nexoruta_api USING(
 nexoruta_app.setting('actor_kind')='operador' AND "OperadorId"=nexoruta_app.ctx_uuid('org_id') OR
 nexoruta_app.setting('actor_kind')='comercio' AND "ComercioId"=nexoruta_app.ctx_uuid('org_id') AND "Estado"='Confirmada' AND "OperadorId"=nexoruta_app.ctx_uuid('selected_operator_id'));
CREATE POLICY settlement_line_read ON public."LiquidacionLineas" FOR SELECT TO nexoruta_api USING(EXISTS(SELECT 1 FROM public."Liquidaciones" l WHERE l."Id"="LiquidacionLineas"."LiquidacionId"));
CREATE POLICY settlement_claim_read ON public."CargosLiquidados" FOR SELECT TO nexoruta_api USING(EXISTS(SELECT 1 FROM public."Liquidaciones" l WHERE l."Id"="CargosLiquidados"."LiquidacionId"));
CREATE POLICY settlement_transition_read ON public."LiquidacionTransiciones" FOR SELECT TO nexoruta_api USING(EXISTS(SELECT 1 FROM public."Liquidaciones" l WHERE l."Id"="LiquidacionTransiciones"."LiquidacionId"));
CREATE POLICY settlement_correction_read ON public."CorreccionesCuenta" FOR SELECT TO nexoruta_api USING(
 nexoruta_app.setting('actor_kind')='operador' AND "OperadorId"=nexoruta_app.ctx_uuid('org_id') OR
 nexoruta_app.setting('actor_kind')='comercio' AND "ComercioId"=nexoruta_app.ctx_uuid('org_id') AND "OperadorId"=nexoruta_app.ctx_uuid('selected_operator_id'));
DO $m$ DECLARE t text; BEGIN
 FOREACH t IN ARRAY ARRAY['Envios','Bultos','TarifasEnvio'] LOOP
 EXECUTE format('GRANT SELECT ON public.%I TO nexoruta_shipment_fn',t);
 EXECUTE format('CREATE POLICY settlement_source ON public.%I FOR SELECT TO nexoruta_shipment_fn USING(nexoruta_app.setting(''actor_kind'')=''operador'' AND "OperadorId"=nexoruta_app.ctx_uuid(''org_id''))',t);
 END LOOP;
END $m$;
GRANT UPDATE("Id") ON public."Envios" TO nexoruta_shipment_fn;
CREATE POLICY settlement_lock_source ON public."Envios" FOR UPDATE TO nexoruta_shipment_fn USING(nexoruta_app.setting('actor_kind')='operador' AND "OperadorId"=nexoruta_app.ctx_uuid('org_id')) WITH CHECK(false);
GRANT SELECT ON public."AccesosUsuario",public."RolesPermisos",public."Permisos" TO nexoruta_shipment_fn;
CREATE POLICY settlement_actor ON public."AccesosUsuario" FOR SELECT TO nexoruta_shipment_fn USING("Id"=nexoruta_app.ctx_uuid('access_id') AND "UsuarioId"=nexoruta_app.ctx_uuid('user_id') AND "OperadorId"=nexoruta_app.ctx_uuid('org_id') AND "Activo");
CREATE POLICY settlement_grants ON public."RolesPermisos" FOR SELECT TO nexoruta_shipment_fn USING(EXISTS(SELECT 1 FROM public."AccesosUsuario" a WHERE a."RolId"="RolesPermisos"."RolId"));
CREATE POLICY settlement_permissions ON public."Permisos" FOR SELECT TO nexoruta_shipment_fn USING("Codigo"='envios.gestionar');
CREATE FUNCTION nexoruta_app.require_settlement_manager() RETURNS void LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF nexoruta_app.setting('actor_kind') IS DISTINCT FROM 'operador' OR NOT EXISTS(
 SELECT 1 FROM public."AccesosUsuario" a JOIN public."RolesPermisos" r ON r."RolId"=a."RolId" JOIN public."Permisos" p ON p."Id"=r."PermisoId"
 WHERE a."Id"=nexoruta_app.ctx_uuid('access_id') AND a."UsuarioId"=nexoruta_app.ctx_uuid('user_id') AND a."OperadorId"=nexoruta_app.ctx_uuid('org_id') AND a."Activo" AND p."Codigo"='envios.gestionar') THEN RAISE EXCEPTION 'Settlement management denied' USING ERRCODE='42501'; END IF;
END $$;
ALTER FUNCTION nexoruta_app.require_settlement_manager() OWNER TO nexoruta_shipment_fn;
REVOKE ALL ON FUNCTION nexoruta_app.require_settlement_manager() FROM PUBLIC;
-- View reads original persisted amounts with a stable predecessor, without repricing.
CREATE VIEW public."MovimientosCuenta" WITH(security_invoker=true) AS
 WITH snapshots AS(
 SELECT t.*,lag((t."Resultado"->>'TotalFinalUyu')::numeric,1,0::numeric) OVER(PARTITION BY t."OperadorId",t."EnvioId" ORDER BY t."CreadoEnUtc",t."Id") anterior,
 row_number() OVER(PARTITION BY t."OperadorId",t."EnvioId" ORDER BY t."CreadoEnUtc",t."Id") numero
 FROM public."TarifasEnvio" t)
 SELECT t."Id",t."OperadorId",e."ComercioId",t."EnvioId",t."CreadoEnUtc" AS "FechaUtc",
 CASE WHEN t.numero=1 THEN CASE WHEN e."EsRetiro" THEN 'Admisión retiro' ELSE 'Admisión entrega' END WHEN t."Servicio" ? 'RutaRecargoId' THEN 'Recargo nueva visita' ELSE 'Ajuste destino' END AS "Concepto",
 ((t."Resultado"->>'TotalFinalUyu')::numeric-t.anterior)::numeric(14,2) AS "ImporteUyu",(t."Resultado"->>'TotalFinalUyu')::numeric AS "TotalPersistidoUyu",
 CASE WHEN e."Estado" IN ('Cancelado','EnDevolucion','Devuelto','Extraviado') OR EXISTS(SELECT 1 FROM public."Bultos" b WHERE b."OperadorId"=e."OperadorId" AND b."EnvioId"=e."Id" AND b."Resultado" IN ('EnDevolucion','Devuelto','Extraviado')) THEN 'Retenido: falta regla económica para cancelación, devolución o extravío'
 WHEN e."Estado"='EntregadoComercio' AND e."EsRetiro" OR e."Estado"='Entregado' AND NOT EXISTS(SELECT 1 FROM public."Bultos" b WHERE b."OperadorId"=e."OperadorId" AND b."EnvioId"=e."Id" AND b."Resultado"<>'Entregado') THEN 'Liquidable'
 ELSE 'Retenido: operación o prueba pendiente' END AS "Tratamiento",
 jsonb_build_object('tarifaId',t."Id",'versionTarifarioId',t."VersionTarifarioId",'servicio',t."Servicio",'resultado',t."Resultado",'cobertura',t."Cobertura",'totalAnteriorUyu',t.anterior)::text AS "Fuente"
 FROM snapshots t JOIN public."Envios" e ON e."OperadorId"=t."OperadorId" AND e."Id"=t."EnvioId"
 UNION ALL SELECT c."Id",c."OperadorId",c."ComercioId",c."EnvioId",c."FechaUtc",'Corrección: '||c."Motivo",c."ImporteUyu",c."ImporteUyu",'Liquidable',jsonb_build_object('movimientoOriginalId',c."MovimientoOriginalId",'actorId',c."ActorId",'motivo',c."Motivo")::text FROM public."CorreccionesCuenta" c;
GRANT SELECT ON public."MovimientosCuenta" TO nexoruta_api,nexoruta_shipment_fn;
CREATE FUNCTION nexoruta_app.prepare_settlement(p_id uuid,p_commerce uuid,p_from timestamptz,p_to timestamptz) RETURNS uuid
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE op uuid:=nexoruta_app.ctx_uuid('org_id'); doc public."Liquidaciones"; cut timestamptz:=clock_timestamp();
BEGIN
 PERFORM nexoruta_app.require_settlement_manager();
 IF p_id IS NULL OR p_commerce IS NULL OR p_from IS NULL OR p_to IS NULL OR p_to<=p_from OR p_to-p_from>interval '366 days' THEN RAISE EXCEPTION 'Invalid settlement period' USING ERRCODE='22023'; END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended(op::text||':'||p_commerce::text,0));
 SELECT * INTO doc FROM public."Liquidaciones" WHERE "Id"=p_id;
 IF FOUND THEN
 IF doc."ComercioId"<>p_commerce OR doc."DesdeUtc"<>p_from OR doc."HastaUtc"<>p_to THEN RAISE EXCEPTION 'Request identity reused' USING ERRCODE='NXR02'; END IF;
 RETURN p_id;
 END IF;
 IF NOT EXISTS(SELECT 1 FROM public."Envios" WHERE "OperadorId"=op AND "ComercioId"=p_commerce) THEN RAISE EXCEPTION 'No eligible commerce account' USING ERRCODE='22023'; END IF;
 INSERT INTO public."Liquidaciones"("Id","OperadorId","ComercioId","Estado","DesdeUtc","HastaUtc","CorteUtc","TotalUyu","PreparadaPor") VALUES(p_id,op,p_commerce,'Borrador',p_from,p_to,cut,0,nexoruta_app.ctx_uuid('user_id'));
 INSERT INTO public."LiquidacionLineas"
 SELECT op,p_id,m."Id",m."EnvioId",m."FechaUtc",m."Concepto",m."ImporteUyu",m."Fuente" FROM public."MovimientosCuenta" m
 WHERE m."OperadorId"=op AND m."ComercioId"=p_commerce AND m."FechaUtc">=p_from AND m."FechaUtc"<p_to AND m."FechaUtc"<=cut AND m."Tratamiento"='Liquidable'
 AND NOT EXISTS(SELECT 1 FROM public."CargosLiquidados" c WHERE c."MovimientoId"=m."Id");
 IF NOT FOUND THEN RAISE EXCEPTION 'No hay movimientos liquidables en el período.' USING ERRCODE='22023'; END IF;
 IF (SELECT count(*) FROM public."LiquidacionLineas" WHERE "LiquidacionId"=p_id)>1000 THEN RAISE EXCEPTION 'El documento supera 1000 conceptos; prepará un período menor.' USING ERRCODE='22023'; END IF;
 UPDATE public."Liquidaciones" SET "TotalUyu"=(SELECT sum("ImporteUyu") FROM public."LiquidacionLineas" WHERE "LiquidacionId"=p_id) WHERE "Id"=p_id;
 INSERT INTO public."LiquidacionTransiciones"("OperadorId","LiquidacionId","Estado","ActorId") VALUES(op,p_id,'Borrador',nexoruta_app.ctx_uuid('user_id'));
 RETURN p_id;
END $$;
ALTER FUNCTION nexoruta_app.prepare_settlement(uuid,uuid,timestamptz,timestamptz) OWNER TO nexoruta_shipment_fn;
REVOKE ALL ON FUNCTION nexoruta_app.prepare_settlement(uuid,uuid,timestamptz,timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nexoruta_app.prepare_settlement(uuid,uuid,timestamptz,timestamptz) TO nexoruta_api;
CREATE FUNCTION nexoruta_app.transition_settlement(p_id uuid,p_confirm boolean) RETURNS boolean
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE doc public."Liquidaciones"; target text:=CASE WHEN p_confirm THEN 'Confirmada' ELSE 'Descartada' END;
BEGIN
 PERFORM nexoruta_app.require_settlement_manager();
 SELECT * INTO doc FROM public."Liquidaciones" WHERE "Id"=p_id;
 IF NOT FOUND THEN RETURN false; END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended(doc."OperadorId"::text||':'||doc."ComercioId"::text,0));
 SELECT * INTO STRICT doc FROM public."Liquidaciones" WHERE "Id"=p_id FOR UPDATE;
 IF doc."Estado"=target THEN RETURN true; END IF;
 IF doc."Estado"<>'Borrador' THEN RAISE EXCEPTION 'El documento ya no es un borrador.' USING ERRCODE='NXR02'; END IF;
 IF p_confirm THEN
 PERFORM 1 FROM public."Envios" e WHERE e."OperadorId"=doc."OperadorId" AND e."Id" IN(SELECT l."EnvioId" FROM public."LiquidacionLineas" l WHERE l."LiquidacionId"=p_id) ORDER BY e."Id" FOR UPDATE;
 IF EXISTS(SELECT 1 FROM public."LiquidacionLineas" l LEFT JOIN public."MovimientosCuenta" m ON m."Id"=l."MovimientoId"
 WHERE l."LiquidacionId"=p_id AND (m."Id" IS NULL OR m."Tratamiento"<>'Liquidable' OR m."ImporteUyu"<>l."ImporteUyu" OR m."Fuente"<>l."Fuente"))
 OR EXISTS(SELECT 1 FROM public."LiquidacionLineas" l JOIN public."CargosLiquidados" c ON c."MovimientoId"=l."MovimientoId" WHERE l."LiquidacionId"=p_id)
 THEN RAISE EXCEPTION 'Los conceptos cambiaron o ya fueron liquidados. Descartá y prepará otro borrador.' USING ERRCODE='NXR02'; END IF;
 INSERT INTO public."CargosLiquidados" SELECT "MovimientoId","OperadorId","LiquidacionId" FROM public."LiquidacionLineas" WHERE "LiquidacionId"=p_id;
 END IF;
 UPDATE public."Liquidaciones" SET "Estado"=target,"ConfirmadaPor"=CASE WHEN p_confirm THEN nexoruta_app.ctx_uuid('user_id') END,"ConfirmadaEnUtc"=CASE WHEN p_confirm THEN clock_timestamp() END WHERE "Id"=p_id;
 INSERT INTO public."LiquidacionTransiciones"("OperadorId","LiquidacionId","Estado","ActorId") VALUES(doc."OperadorId",p_id,target,nexoruta_app.ctx_uuid('user_id'));
 RETURN true;
END $$;
ALTER FUNCTION nexoruta_app.transition_settlement(uuid,boolean) OWNER TO nexoruta_shipment_fn;
REVOKE ALL ON FUNCTION nexoruta_app.transition_settlement(uuid,boolean) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nexoruta_app.transition_settlement(uuid,boolean) TO nexoruta_api;
CREATE FUNCTION nexoruta_app.correct_settled_movement(p_id uuid,p_original uuid,p_amount numeric,p_reason text) RETURNS uuid
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE l record; existing public."CorreccionesCuenta";
BEGIN
 PERFORM nexoruta_app.require_settlement_manager();
 IF p_id IS NULL OR p_amount IS NULL OR p_amount=0 OR round(p_amount,2)<>p_amount OR abs(p_amount)>999999999999.99 OR length(btrim(p_reason)) NOT BETWEEN 10 AND 500 OR p_reason IS NULL THEN RAISE EXCEPTION 'La corrección requiere importe con dos decimales y motivo de 10 a 500 caracteres.' USING ERRCODE='22023'; END IF;
 SELECT x.*,d."ComercioId" INTO l FROM public."CargosLiquidados" c JOIN public."LiquidacionLineas" x ON x."MovimientoId"=c."MovimientoId" AND x."LiquidacionId"=c."LiquidacionId" JOIN public."Liquidaciones" d ON d."Id"=c."LiquidacionId" WHERE c."MovimientoId"=p_original;
 IF NOT FOUND THEN RAISE EXCEPTION 'El movimiento confirmado no está disponible.' USING ERRCODE='22023'; END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended(l."OperadorId"::text||':'||l."ComercioId"::text,0));
 SELECT * INTO existing FROM public."CorreccionesCuenta" WHERE "Id"=p_id;
 IF FOUND THEN
 IF existing."MovimientoOriginalId"<>p_original OR existing."ImporteUyu"<>p_amount OR existing."Motivo"<>btrim(p_reason) THEN RAISE EXCEPTION 'Request identity reused' USING ERRCODE='NXR02'; END IF;
 RETURN p_id;
 END IF;
 INSERT INTO public."CorreccionesCuenta"("Id","OperadorId","ComercioId","EnvioId","MovimientoOriginalId","ImporteUyu","Motivo","ActorId") VALUES(p_id,l."OperadorId",l."ComercioId",l."EnvioId",p_original,p_amount,btrim(p_reason),nexoruta_app.ctx_uuid('user_id'));
 RETURN p_id;
END $$;
ALTER FUNCTION nexoruta_app.correct_settled_movement(uuid,uuid,numeric,text) OWNER TO nexoruta_shipment_fn;
REVOKE ALL ON FUNCTION nexoruta_app.correct_settled_movement(uuid,uuid,numeric,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nexoruta_app.correct_settled_movement(uuid,uuid,numeric,text) TO nexoruta_api;
CREATE FUNCTION public.guard_settlement_history() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
 IF TG_TABLE_NAME<>'Liquidaciones' OR OLD."Estado"<>'Borrador' THEN RAISE EXCEPTION 'Confirmed economic history is immutable' USING ERRCODE='55000'; END IF;
 RETURN NEW;
END $$;
ALTER FUNCTION public.guard_settlement_history() OWNER TO nexoruta_schema_owner;
REVOKE ALL ON FUNCTION public.guard_settlement_history() FROM PUBLIC;
CREATE TRIGGER immutable_settlement BEFORE UPDATE OR DELETE ON public."Liquidaciones" FOR EACH ROW EXECUTE FUNCTION public.guard_settlement_history();
DO $m$ DECLARE t text; BEGIN FOREACH t IN ARRAY ARRAY['LiquidacionLineas','CargosLiquidados','LiquidacionTransiciones','CorreccionesCuenta'] LOOP
 EXECUTE format('CREATE TRIGGER immutable_settlement BEFORE UPDATE OR DELETE ON public.%I FOR EACH ROW EXECUTE FUNCTION public.guard_settlement_history()',t);
 END LOOP;
 FOREACH t IN ARRAY ARRAY['Liquidaciones','LiquidacionLineas','CargosLiquidados','LiquidacionTransiciones','CorreccionesCuenta','MovimientosCuenta'] LOOP EXECUTE format('ALTER %s public.%I OWNER TO nexoruta_schema_owner',CASE WHEN t='MovimientosCuenta' THEN 'VIEW' ELSE 'TABLE' END,t); END LOOP;
END $m$;
""");
    protected override void Down(MigrationBuilder m) => throw new NotSupportedException("Economic documents and corrections must remain immutable.");
}
