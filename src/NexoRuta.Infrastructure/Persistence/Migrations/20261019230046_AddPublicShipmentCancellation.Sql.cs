using Microsoft.EntityFrameworkCore.Migrations;
namespace NexoRuta.Infrastructure.Persistence.Migrations;
public partial class AddPublicShipmentCancellation
{
    private static void ConfigurePublicCancellation(MigrationBuilder migration) => migration.Sql("""
DO $$ BEGIN
IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='nexoruta_cancel_public_fn') THEN
    CREATE ROLE nexoruta_cancel_public_fn NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
END IF;
END $$;
ALTER ROLE nexoruta_cancel_public_fn NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
GRANT USAGE ON SCHEMA public,nexoruta_app TO nexoruta_cancel_public_fn;
GRANT EXECUTE ON FUNCTION nexoruta_app.setting(text),nexoruta_app.ctx_uuid(text) TO nexoruta_cancel_public_fn;
GRANT SELECT ON public."EnlacesSeguimiento",public."Envios",public."Destinatarios",public."Operadores",public."OutboxMessages",public."CodigosCancelacionPublica" TO nexoruta_cancel_public_fn;
GRANT UPDATE ("Id") ON public."Operadores" TO nexoruta_cancel_public_fn;
GRANT INSERT ON public."CodigosCancelacionPublica",public."OutboxMessages" TO nexoruta_cancel_public_fn;
GRANT UPDATE ("Activo","Intentos","ConsumidoEnUtc","CorreoEnviadoEnUtc") ON public."CodigosCancelacionPublica" TO nexoruta_cancel_public_fn;
ALTER TABLE public."CodigosCancelacionPublica" OWNER TO nexoruta_schema_owner;
ALTER TABLE public."CodigosCancelacionPublica" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."CodigosCancelacionPublica" FORCE ROW LEVEL SECURITY;
REVOKE ALL ON public."CodigosCancelacionPublica" FROM PUBLIC;
CREATE POLICY public_cancel_code ON public."CodigosCancelacionPublica" TO nexoruta_cancel_public_fn
    USING (("EnvioId"=nexoruta_app.ctx_uuid('public_cancel_envio') AND "OperadorId"=nexoruta_app.ctx_uuid('public_cancel_operador')) OR "MensajeId"=nexoruta_app.ctx_uuid('public_cancel_message'))
    WITH CHECK ("EnvioId"=nexoruta_app.ctx_uuid('public_cancel_envio') AND "OperadorId"=nexoruta_app.ctx_uuid('public_cancel_operador'));
CREATE POLICY public_cancel_link ON public."EnlacesSeguimiento" FOR SELECT TO nexoruta_cancel_public_fn USING ("TokenHash"=nexoruta_app.setting('public_cancel_hash'));
CREATE POLICY public_cancel_envio ON public."Envios" FOR SELECT TO nexoruta_cancel_public_fn USING ("Id"=nexoruta_app.ctx_uuid('public_cancel_envio') AND "OperadorId"=nexoruta_app.ctx_uuid('public_cancel_operador'));
CREATE POLICY public_cancel_recipient ON public."Destinatarios" FOR SELECT TO nexoruta_cancel_public_fn USING (
    EXISTS(SELECT 1 FROM public."Envios" e WHERE e."DestinatarioId"="Destinatarios"."Id" AND e."OperadorId"="Destinatarios"."OperadorId"));
CREATE POLICY public_cancel_operator ON public."Operadores" TO nexoruta_cancel_public_fn USING ("Id"=nexoruta_app.ctx_uuid('public_cancel_operador')) WITH CHECK(false);
CREATE POLICY public_cancel_outbox ON public."OutboxMessages" TO nexoruta_cancel_public_fn
    USING ("Id"=nexoruta_app.ctx_uuid('public_cancel_message'))
    WITH CHECK ("EnvioId"=nexoruta_app.ctx_uuid('public_cancel_envio') AND "OperadorId"=nexoruta_app.ctx_uuid('public_cancel_operador')
        AND "Ambito"='Envio' AND "ClaveAmbito"="EnvioId" AND "Tipo"='CodigoCancelacionEmail' AND "Destino"='RabbitMq' AND "VersionContrato"=1 AND "Estado"='Pendiente' AND "EstadoEntrega" IS NULL AND "Intentos"=0 AND "Prioridad"='Alta');
GRANT SELECT ON public."CodigosCancelacionPublica" TO nexoruta_cancel_fn;
CREATE POLICY cancellation_verified_code ON public."CodigosCancelacionPublica" FOR SELECT TO nexoruta_cancel_fn USING ("Id"=nexoruta_app.ctx_uuid('cancel_public_code_id'));
CREATE POLICY cancellation_public_shipment ON public."Envios" TO nexoruta_cancel_fn
    USING ("Id"=nexoruta_app.ctx_uuid('cancel_shipment_id') AND EXISTS(SELECT 1 FROM public."CodigosCancelacionPublica" c WHERE c."EnvioId"="Envios"."Id" AND c."OperadorId"="Envios"."OperadorId" AND c."Activo" AND c."ConsumidoEnUtc" IS NOT NULL))
    WITH CHECK("Id"=nexoruta_app.ctx_uuid('cancel_shipment_id') AND "Estado"='Cancelado' AND "EntregaFisicaEnUtc" IS NULL);
CREATE POLICY cancellation_public_audit ON public."CancelacionesEnvio" TO nexoruta_cancel_fn
    USING ("AutorizacionPublicaId"=nexoruta_app.ctx_uuid('cancel_public_code_id'))
    WITH CHECK ("AutorizacionPublicaId"=nexoruta_app.ctx_uuid('cancel_public_code_id') AND "AccesoId" IS NULL AND "UsuarioId" IS NULL AND "TipoActor" IS NULL AND "OrganizacionActorId" IS NULL
        AND EXISTS(SELECT 1 FROM public."CodigosCancelacionPublica" c WHERE c."Id"="AutorizacionPublicaId" AND c."EnvioId"="CancelacionesEnvio"."EnvioId" AND c."OperadorId"="CancelacionesEnvio"."OperadorId" AND c."ConsumidoEnUtc" IS NOT NULL));
CREATE FUNCTION nexoruta_app.apply_shipment_cancellation(p_envio uuid, p_version bigint, p_motivo text, p_id uuid, p_aviso uuid, p_mensaje uuid, p_publico uuid)
            RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
            AS $cancel$
            DECLARE e public."Envios"%ROWTYPE; a public."CancelacionesEnvio"%ROWTYPE;
                v_acceso uuid := nexoruta_app.ctx_uuid('access_id'); v_usuario uuid := nexoruta_app.ctx_uuid('user_id');
                v_org uuid := nexoruta_app.ctx_uuid('org_id'); v_actor text := nexoruta_app.setting('actor_kind');
                v_ruta uuid; v_repartidor uuid; v_ahora timestamptz := clock_timestamp();
            BEGIN
                PERFORM set_config('nexoruta.cancel_public_code_id', COALESCE(p_publico::text,''), true);
                IF p_publico IS NOT NULL THEN
                    IF NOT EXISTS (SELECT 1 FROM public."CodigosCancelacionPublica" c WHERE c."Id"=p_publico AND c."EnvioId"=p_envio AND c."Activo" AND c."ConsumidoEnUtc" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Public cancellation was not verified.' USING ERRCODE='42501';
                    END IF;
                    v_acceso := NULL; v_usuario := NULL; v_org := NULL;
                END IF;
                IF p_version < 0 OR p_version IS NULL OR p_motivo IS NULL OR length(btrim(p_motivo)) NOT BETWEEN 1 AND 500
                    OR p_id IS NULL OR p_aviso IS NULL OR p_mensaje IS NULL OR (p_publico IS NULL AND (v_actor IS NULL OR v_actor NOT IN ('operador','comercio'))) THEN
                    RAISE EXCEPTION 'Cancellation context is invalid.' USING ERRCODE = '42501';
                END IF;
                PERFORM set_config('nexoruta.cancel_shipment_id', p_envio::text, true);
                SELECT * INTO e FROM public."Envios" WHERE "Id" = p_envio;
                IF NOT FOUND THEN RAISE EXCEPTION 'Shipment is outside the account.' USING ERRCODE = '42501'; END IF;
                PERFORM set_config('nexoruta.cancel_operator_id', e."OperadorId"::text, true);
                PERFORM 1 FROM public."Operadores" WHERE "Id" = e."OperadorId" FOR UPDATE;
                IF NOT FOUND THEN RAISE EXCEPTION 'Operator is unavailable.' USING ERRCODE = '42501'; END IF;
                IF p_publico IS NULL AND NOT EXISTS (SELECT 1 FROM public."AccesosUsuario" x JOIN public."Roles" r ON r."Id" = x."RolId"
                    JOIN public."RolesPermisos" rp ON rp."RolId" = r."Id" JOIN public."Permisos" p ON p."Id" = rp."PermisoId"
                    WHERE x."Id" = v_acceso AND x."UsuarioId" = v_usuario AND x."Activo"
                        AND ((v_actor = 'comercio' AND x."ComercioId" = e."ComercioId" AND x."ComercioId" = v_org AND p."Codigo" = 'envios.crear')
                        OR (v_actor = 'operador' AND x."OperadorId" = e."OperadorId" AND x."OperadorId" = v_org
                            AND r."Codigo" = 'administrador_operador' AND r."EsProtegidoSistema" AND p."Codigo" = 'envios.gestionar'))) THEN
                    RAISE EXCEPTION 'Cancellation is not authorized.' USING ERRCODE = '42501';
                END IF;
                SELECT * INTO e FROM public."Envios" WHERE "Id" = p_envio FOR UPDATE;
                IF e."Estado" = 'Cancelado' THEN
                    SELECT * INTO a FROM public."CancelacionesEnvio" WHERE "EnvioId" = p_envio AND "OperadorId" = e."OperadorId";
                    IF FOUND AND a."AccesoId" = v_acceso AND a."VersionAnterior" = p_version AND a."Motivo" = btrim(p_motivo)
                        AND a."VersionEnvio" = e."Version" THEN RETURN a."Id"; END IF;
                    RAISE EXCEPTION 'Cancellation changed.' USING ERRCODE = 'NXR02';
                END IF;
                IF e."Version" <> p_version OR e."EntregaFisicaEnUtc" IS NOT NULL
                    OR e."Estado" NOT IN ('Admitido','EnDeposito','AsignadoRuta','EnTransito','NoEntregado','Reprogramado') THEN
                    RAISE EXCEPTION 'Shipment no longer allows cancellation.' USING ERRCODE = 'NXR02';
                END IF;
                IF (SELECT count(DISTINCT b."RutaCustodiaId") FROM public."Bultos" b
                    WHERE b."EnvioId" = p_envio AND b."Custodia" IN ('Repartidor','PendienteDeposito')) > 1 THEN
                    RAISE EXCEPTION 'Shipment has inconsistent custody.' USING ERRCODE = 'NXR02';
                END IF;
                SELECT h."Id", h."RepartidorAccesoId" INTO v_ruta, v_repartidor FROM public."HojasRuta" h
                    WHERE h."OperadorId" = e."OperadorId" AND h."Estado" IN ('Despachada','EnCurso')
                        AND (EXISTS (SELECT 1 FROM public."Bultos" b WHERE b."EnvioId" = p_envio
                            AND b."RutaCustodiaId" = h."Id" AND b."Custodia" IN ('Repartidor','PendienteDeposito'))
                            OR EXISTS (SELECT 1 FROM public."ParadasRuta" p WHERE p."EnvioId" = p_envio AND p."RutaId" = h."Id" AND p."Activa"))
                    ORDER BY EXISTS (SELECT 1 FROM public."Bultos" b WHERE b."EnvioId" = p_envio AND b."RutaCustodiaId" = h."Id"
                        AND b."Custodia" IN ('Repartidor','PendienteDeposito')) DESC LIMIT 1;
                UPDATE public."HojasRuta" h SET "Version" = h."Version" + 1 WHERE h."OperadorId" = e."OperadorId"
                    AND (h."Id" = v_ruta OR EXISTS (SELECT 1 FROM public."ParadasRuta" p WHERE p."RutaId" = h."Id" AND p."EnvioId" = p_envio AND p."Activa"));
                UPDATE public."ParadasRuta" SET "Activa" = false, "MotivoSuspension" = btrim(p_motivo)
                    WHERE "EnvioId" = p_envio AND "OperadorId" = e."OperadorId" AND "Activa";
                UPDATE public."Envios" SET "Estado" = 'Cancelado', "Version" = p_version + 1 WHERE "Id" = p_envio;
                INSERT INTO public."CancelacionesEnvio" ("Id","OperadorId","EnvioId","AccesoId","UsuarioId","TipoActor","OrganizacionActorId",
                    "Motivo","EstadoAnterior","VersionAnterior","VersionEnvio","CreadaEnUtc","AutorizacionPublicaId")
                    VALUES (p_id,e."OperadorId",p_envio,v_acceso,v_usuario,CASE WHEN p_publico IS NOT NULL THEN NULL WHEN v_actor = 'operador' THEN 'Operador' ELSE 'Comercio' END,
                        v_org,btrim(p_motivo),e."Estado",p_version,p_version+1,v_ahora,p_publico);
                IF v_ruta IS NOT NULL THEN
                    INSERT INTO public."AvisosReparto" ("Id","OperadorId","AccesoId","RutaId","EnvioId","VersionEnvio","Tipo","Motivo","CreadoEnUtc")
                        VALUES(p_aviso,e."OperadorId",v_repartidor,v_ruta,p_envio,p_version+1,'Cancelacion',btrim(p_motivo),v_ahora);
                    INSERT INTO public."OutboxMessages" ("Id","OperadorId","EnvioId","Ambito","ClaveAmbito","Tipo","VersionContrato","Destino",
                        "Prioridad","Estado","Payload","Intentos","CreadoEnUtc","ProximoIntentoEnUtc")
                        VALUES(p_mensaje,e."OperadorId",p_envio,'Envio',p_envio,'RepartoModificado',1,'PushFcm','Alta','Pendiente',
                            jsonb_build_object('avisoId',p_aviso),0,v_ahora,v_ahora);
                END IF;
                RETURN p_id;
            END $cancel$;
ALTER FUNCTION nexoruta_app.apply_shipment_cancellation(uuid,bigint,text,uuid,uuid,uuid,uuid) OWNER TO nexoruta_cancel_fn;
REVOKE ALL ON FUNCTION nexoruta_app.apply_shipment_cancellation(uuid,bigint,text,uuid,uuid,uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nexoruta_app.apply_shipment_cancellation(uuid,bigint,text,uuid,uuid,uuid,uuid) TO nexoruta_cancel_public_fn;
DROP FUNCTION nexoruta_app.cancel_shipment(uuid,bigint,text,uuid,uuid,uuid);
CREATE FUNCTION nexoruta_app.cancel_shipment(p_envio uuid,p_version bigint,p_motivo text,p_id uuid,p_aviso uuid,p_mensaje uuid)
RETURNS uuid LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog,nexoruta_app
AS $$ SELECT nexoruta_app.apply_shipment_cancellation(p_envio,p_version,p_motivo,p_id,p_aviso,p_mensaje,NULL) $$;
ALTER FUNCTION nexoruta_app.cancel_shipment(uuid,bigint,text,uuid,uuid,uuid) OWNER TO nexoruta_cancel_fn;
REVOKE ALL ON FUNCTION nexoruta_app.cancel_shipment(uuid,bigint,text,uuid,uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nexoruta_app.cancel_shipment(uuid,bigint,text,uuid,uuid,uuid) TO nexoruta_api;
CREATE FUNCTION nexoruta_app.prepare_public_cancel_scope(p_token text) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,nexoruta_app AS $$
DECLARE l public."EnlacesSeguimiento"%ROWTYPE;
BEGIN
    PERFORM set_config('nexoruta.public_cancel_envio','',true);
    PERFORM set_config('nexoruta.public_cancel_operador','',true);
    PERFORM set_config('nexoruta.public_cancel_message','',true);
    IF p_token IS NULL OR p_token !~ '^[0-9a-f]{64}$' THEN RETURN NULL; END IF;
    PERFORM set_config('nexoruta.public_cancel_hash',encode(public.digest(convert_to(p_token,'SQL_ASCII'),'sha256'),'hex'),true);
    SELECT * INTO l FROM public."EnlacesSeguimiento" WHERE "TokenHash"=nexoruta_app.setting('public_cancel_hash')
        AND "RevocadoEnUtc" IS NULL AND "CreadoEnUtc"<=clock_timestamp() AND "ExpiraEnUtc">clock_timestamp();
    IF NOT FOUND THEN RETURN NULL; END IF;
    PERFORM set_config('nexoruta.public_cancel_envio',l."EnvioId"::text,true);
    PERFORM set_config('nexoruta.public_cancel_operador',l."OperadorId"::text,true);
    PERFORM 1 FROM public."Operadores" WHERE "Id"=l."OperadorId" FOR UPDATE;
    RETURN l."EnvioId";
END $$;
CREATE FUNCTION nexoruta_app.public_cancellation_email(p_token text) RETURNS text
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,nexoruta_app AS $$
DECLARE v_id uuid; v_email text;
BEGIN
    v_id:=nexoruta_app.prepare_public_cancel_scope(p_token);
    IF v_id IS NULL THEN RETURN NULL; END IF;
    SELECT d."Email" INTO v_email FROM public."Envios" e JOIN public."Destinatarios" d ON d."Id"=e."DestinatarioId" AND d."OperadorId"=e."OperadorId"
        WHERE e."Id"=v_id AND e."EntregaFisicaEnUtc" IS NULL AND e."Estado" IN ('Admitido','EnDeposito','AsignadoRuta','EnTransito','NoEntregado','Reprogramado');
    RETURN v_email;
END $$;
CREATE FUNCTION nexoruta_app.issue_public_cancel_code(p_token text,p_codigo text,p_id uuid,p_mensaje uuid,p_protected text) RETURNS text
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,nexoruta_app AS $$
DECLARE v_id uuid; v_op uuid; v_now timestamptz:=clock_timestamp();
BEGIN
    v_id:=nexoruta_app.prepare_public_cancel_scope(p_token); v_op:=nexoruta_app.ctx_uuid('public_cancel_operador');
    IF v_id IS NULL OR NOT EXISTS (SELECT 1 FROM public."Envios" WHERE "Id"=v_id AND "EntregaFisicaEnUtc" IS NULL
        AND "Estado" IN ('Admitido','EnDeposito','AsignadoRuta','EnTransito','NoEntregado','Reprogramado')) THEN RETURN 'NoDisponible'; END IF;
    IF p_codigo IS NULL OR p_codigo !~ '^[A-HJ-KM-NP-Z2-9]{12}$' OR p_id IS NULL OR p_mensaje IS NULL OR p_protected IS NULL OR length(p_protected)>32768 THEN
        RAISE EXCEPTION 'Invalid cancellation code envelope.' USING ERRCODE='22023'; END IF;
    IF EXISTS (SELECT 1 FROM public."CodigosCancelacionPublica" WHERE "EnvioId"=v_id AND "CreadoEnUtc">v_now-interval '60 seconds') THEN RETURN 'Frecuente'; END IF;
    UPDATE public."CodigosCancelacionPublica" SET "Activo"=false WHERE "EnvioId"=v_id AND "Activo";
    INSERT INTO public."OutboxMessages" ("Id","OperadorId","EnvioId","Ambito","ClaveAmbito","Tipo","VersionContrato","Destino","Prioridad","Estado","Payload","Intentos","CreadoEnUtc","ProximoIntentoEnUtc")
        VALUES(p_mensaje,v_op,v_id,'Envio',v_id,'CodigoCancelacionEmail',1,'RabbitMq','Alta','Pendiente',jsonb_build_object('protectedCommand',p_protected),0,v_now,v_now);
    INSERT INTO public."CodigosCancelacionPublica" ("Id","OperadorId","EnvioId","MensajeId","Hash","CreadoEnUtc","Activo","Intentos")
        VALUES(p_id,v_op,v_id,p_mensaje,public.crypt(p_codigo,public.gen_salt('bf',12)),v_now,true,0);
    RETURN 'Encolado';
END $$;
CREATE FUNCTION nexoruta_app.confirm_public_cancellation(p_token text,p_codigo text,p_id uuid,p_aviso uuid,p_mensaje uuid) RETURNS text
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,nexoruta_app AS $$
DECLARE v_id uuid; c public."CodigosCancelacionPublica"%ROWTYPE; e public."Envios"%ROWTYPE;
BEGIN
    v_id:=nexoruta_app.prepare_public_cancel_scope(p_token);
    IF v_id IS NULL THEN RETURN 'NoDisponible'; END IF;
    SELECT * INTO e FROM public."Envios" WHERE "Id"=v_id;
    SELECT * INTO c FROM public."CodigosCancelacionPublica" WHERE "EnvioId"=v_id AND "Activo" FOR UPDATE;
    IF NOT FOUND THEN RETURN 'CodigoInvalido'; END IF;
    IF c."ConsumidoEnUtc" IS NOT NULL THEN
        IF e."Estado"='Cancelado' AND e."EntregaFisicaEnUtc" IS NULL AND public.crypt(COALESCE(p_codigo,''),c."Hash")=c."Hash" THEN RETURN 'Correcto'; END IF;
        RETURN 'NoDisponible';
    END IF;
    IF e."EntregaFisicaEnUtc" IS NOT NULL OR e."Estado" NOT IN ('Admitido','EnDeposito','AsignadoRuta','EnTransito','NoEntregado','Reprogramado') THEN RETURN 'NoDisponible'; END IF;
    IF c."Intentos">=5 THEN RETURN 'Agotado'; END IF;
    UPDATE public."CodigosCancelacionPublica" SET "Intentos"="Intentos"+1 WHERE "Id"=c."Id";
    IF p_codigo IS NULL OR public.crypt(p_codigo,c."Hash")<>c."Hash" THEN RETURN 'CodigoInvalido'; END IF;
    UPDATE public."CodigosCancelacionPublica" SET "ConsumidoEnUtc"=clock_timestamp() WHERE "Id"=c."Id";
    PERFORM nexoruta_app.apply_shipment_cancellation(v_id,e."Version",'Cancelación solicitada por el destinatario verificado por correo.',p_id,p_aviso,p_mensaje,c."Id");
    RETURN 'Correcto';
END $$;
CREATE FUNCTION nexoruta_app.prepare_public_cancel_email(p_message uuid) RETURNS text
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,nexoruta_app AS $$
DECLARE c public."CodigosCancelacionPublica"%ROWTYPE; v_protected text;
BEGIN
    PERFORM set_config('nexoruta.public_cancel_message',p_message::text,true);
    SELECT * INTO c FROM public."CodigosCancelacionPublica" WHERE "MensajeId"=p_message;
    IF NOT FOUND THEN RETURN NULL; END IF;
    PERFORM set_config('nexoruta.public_cancel_envio',c."EnvioId"::text,true);
    PERFORM set_config('nexoruta.public_cancel_operador',c."OperadorId"::text,true);
    IF NOT c."Activo" OR c."Intentos">=5 OR c."ConsumidoEnUtc" IS NOT NULL OR c."CorreoEnviadoEnUtc" IS NOT NULL THEN RETURN NULL; END IF;
    IF NOT EXISTS(SELECT 1 FROM public."Envios" WHERE "Id"=c."EnvioId" AND "EntregaFisicaEnUtc" IS NULL
        AND "Estado" IN ('Admitido','EnDeposito','AsignadoRuta','EnTransito','NoEntregado','Reprogramado')) THEN RETURN NULL; END IF;
    SELECT "Payload"->>'protectedCommand' INTO v_protected FROM public."OutboxMessages" WHERE "Id"=p_message AND "EnvioId"=c."EnvioId"
        AND "OperadorId"=c."OperadorId" AND "Tipo"='CodigoCancelacionEmail' AND "Destino"='RabbitMq' AND "VersionContrato"=1
        AND "Estado" IN ('Procesando','Publicado');
    RETURN v_protected;
END $$;
CREATE FUNCTION nexoruta_app.mark_public_cancel_email(p_message uuid) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,nexoruta_app AS $$
DECLARE c public."CodigosCancelacionPublica"%ROWTYPE;
BEGIN
    PERFORM set_config('nexoruta.public_cancel_message',p_message::text,true);
    SELECT * INTO c FROM public."CodigosCancelacionPublica" WHERE "MensajeId"=p_message;
    IF NOT FOUND THEN RETURN false; END IF;
    PERFORM set_config('nexoruta.public_cancel_envio',c."EnvioId"::text,true);
    PERFORM set_config('nexoruta.public_cancel_operador',c."OperadorId"::text,true);
    IF NOT EXISTS (SELECT 1 FROM public."OutboxMessages" WHERE "Id"=p_message AND "Tipo"='CodigoCancelacionEmail' AND "Destino"='RabbitMq' AND "VersionContrato"=1 AND "Estado" IN ('Procesando','Publicado')) THEN RETURN false; END IF;
    UPDATE public."CodigosCancelacionPublica" SET "CorreoEnviadoEnUtc"=COALESCE("CorreoEnviadoEnUtc",clock_timestamp()) WHERE "MensajeId"=p_message;
    RETURN FOUND;
END $$;

ALTER FUNCTION nexoruta_app.prepare_public_cancel_scope(text) OWNER TO nexoruta_cancel_public_fn;
REVOKE ALL ON FUNCTION nexoruta_app.prepare_public_cancel_scope(text) FROM PUBLIC;

ALTER FUNCTION nexoruta_app.public_cancellation_email(text) OWNER TO nexoruta_cancel_public_fn;
REVOKE ALL ON FUNCTION nexoruta_app.public_cancellation_email(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nexoruta_app.public_cancellation_email(text) TO nexoruta_api;

ALTER FUNCTION nexoruta_app.issue_public_cancel_code(text,text,uuid,uuid,text) OWNER TO nexoruta_cancel_public_fn;
REVOKE ALL ON FUNCTION nexoruta_app.issue_public_cancel_code(text,text,uuid,uuid,text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nexoruta_app.issue_public_cancel_code(text,text,uuid,uuid,text) TO nexoruta_api;

ALTER FUNCTION nexoruta_app.confirm_public_cancellation(text,text,uuid,uuid,uuid) OWNER TO nexoruta_cancel_public_fn;
REVOKE ALL ON FUNCTION nexoruta_app.confirm_public_cancellation(text,text,uuid,uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nexoruta_app.confirm_public_cancellation(text,text,uuid,uuid,uuid) TO nexoruta_api;

ALTER FUNCTION nexoruta_app.prepare_public_cancel_email(uuid) OWNER TO nexoruta_cancel_public_fn;
REVOKE ALL ON FUNCTION nexoruta_app.prepare_public_cancel_email(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nexoruta_app.prepare_public_cancel_email(uuid) TO nexoruta_worker;

ALTER FUNCTION nexoruta_app.mark_public_cancel_email(uuid) OWNER TO nexoruta_cancel_public_fn;
REVOKE ALL ON FUNCTION nexoruta_app.mark_public_cancel_email(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nexoruta_app.mark_public_cancel_email(uuid) TO nexoruta_worker;

""");
    private static void RemovePublicCancellation(MigrationBuilder migration) => migration.Sql("""
DROP FUNCTION nexoruta_app.mark_public_cancel_email(uuid);
DROP FUNCTION nexoruta_app.prepare_public_cancel_email(uuid);
DROP FUNCTION nexoruta_app.confirm_public_cancellation(text,text,uuid,uuid,uuid);
DROP FUNCTION nexoruta_app.issue_public_cancel_code(text,text,uuid,uuid,text);
DROP FUNCTION nexoruta_app.public_cancellation_email(text);
DROP FUNCTION nexoruta_app.prepare_public_cancel_scope(text);
DROP FUNCTION nexoruta_app.cancel_shipment(uuid,bigint,text,uuid,uuid,uuid);
CREATE FUNCTION nexoruta_app.cancel_shipment(p_envio uuid, p_version bigint, p_motivo text, p_id uuid, p_aviso uuid, p_mensaje uuid)
            RETURNS uuid LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
            AS $cancel$
            DECLARE e public."Envios"%ROWTYPE; a public."CancelacionesEnvio"%ROWTYPE;
                v_acceso uuid := nexoruta_app.ctx_uuid('access_id'); v_usuario uuid := nexoruta_app.ctx_uuid('user_id');
                v_org uuid := nexoruta_app.ctx_uuid('org_id'); v_actor text := nexoruta_app.setting('actor_kind');
                v_ruta uuid; v_repartidor uuid; v_ahora timestamptz := clock_timestamp();
            BEGIN
                IF p_version < 0 OR p_version IS NULL OR p_motivo IS NULL OR length(btrim(p_motivo)) NOT BETWEEN 1 AND 500
                    OR p_id IS NULL OR p_aviso IS NULL OR p_mensaje IS NULL OR v_actor NOT IN ('operador','comercio') THEN
                    RAISE EXCEPTION 'Cancellation context is invalid.' USING ERRCODE = '42501';
                END IF;
                PERFORM set_config('nexoruta.cancel_shipment_id', p_envio::text, true);
                SELECT * INTO e FROM public."Envios" WHERE "Id" = p_envio;
                IF NOT FOUND THEN RAISE EXCEPTION 'Shipment is outside the account.' USING ERRCODE = '42501'; END IF;
                PERFORM set_config('nexoruta.cancel_operator_id', e."OperadorId"::text, true);
                PERFORM 1 FROM public."Operadores" WHERE "Id" = e."OperadorId" FOR UPDATE;
                IF NOT FOUND THEN RAISE EXCEPTION 'Operator is unavailable.' USING ERRCODE = '42501'; END IF;
                IF NOT EXISTS (SELECT 1 FROM public."AccesosUsuario" x JOIN public."Roles" r ON r."Id" = x."RolId"
                    JOIN public."RolesPermisos" rp ON rp."RolId" = r."Id" JOIN public."Permisos" p ON p."Id" = rp."PermisoId"
                    WHERE x."Id" = v_acceso AND x."UsuarioId" = v_usuario AND x."Activo"
                        AND ((v_actor = 'comercio' AND x."ComercioId" = e."ComercioId" AND x."ComercioId" = v_org AND p."Codigo" = 'envios.crear')
                        OR (v_actor = 'operador' AND x."OperadorId" = e."OperadorId" AND x."OperadorId" = v_org
                            AND r."Codigo" = 'administrador_operador' AND r."EsProtegidoSistema" AND p."Codigo" = 'envios.gestionar'))) THEN
                    RAISE EXCEPTION 'Cancellation is not authorized.' USING ERRCODE = '42501';
                END IF;
                SELECT * INTO e FROM public."Envios" WHERE "Id" = p_envio FOR UPDATE;
                IF e."Estado" = 'Cancelado' THEN
                    SELECT * INTO a FROM public."CancelacionesEnvio" WHERE "EnvioId" = p_envio AND "OperadorId" = e."OperadorId";
                    IF FOUND AND a."AccesoId" = v_acceso AND a."VersionAnterior" = p_version AND a."Motivo" = btrim(p_motivo)
                        AND a."VersionEnvio" = e."Version" THEN RETURN a."Id"; END IF;
                    RAISE EXCEPTION 'Cancellation changed.' USING ERRCODE = 'NXR02';
                END IF;
                IF e."Version" <> p_version OR e."EntregaFisicaEnUtc" IS NOT NULL
                    OR e."Estado" NOT IN ('Admitido','EnDeposito','AsignadoRuta','EnTransito','NoEntregado','Reprogramado') THEN
                    RAISE EXCEPTION 'Shipment no longer allows cancellation.' USING ERRCODE = 'NXR02';
                END IF;
                IF (SELECT count(DISTINCT b."RutaCustodiaId") FROM public."Bultos" b
                    WHERE b."EnvioId" = p_envio AND b."Custodia" IN ('Repartidor','PendienteDeposito')) > 1 THEN
                    RAISE EXCEPTION 'Shipment has inconsistent custody.' USING ERRCODE = 'NXR02';
                END IF;
                SELECT h."Id", h."RepartidorAccesoId" INTO v_ruta, v_repartidor FROM public."HojasRuta" h
                    WHERE h."OperadorId" = e."OperadorId" AND h."Estado" IN ('Despachada','EnCurso')
                        AND (EXISTS (SELECT 1 FROM public."Bultos" b WHERE b."EnvioId" = p_envio
                            AND b."RutaCustodiaId" = h."Id" AND b."Custodia" IN ('Repartidor','PendienteDeposito'))
                            OR EXISTS (SELECT 1 FROM public."ParadasRuta" p WHERE p."EnvioId" = p_envio AND p."RutaId" = h."Id" AND p."Activa"))
                    ORDER BY EXISTS (SELECT 1 FROM public."Bultos" b WHERE b."EnvioId" = p_envio AND b."RutaCustodiaId" = h."Id"
                        AND b."Custodia" IN ('Repartidor','PendienteDeposito')) DESC LIMIT 1;
                UPDATE public."HojasRuta" h SET "Version" = h."Version" + 1 WHERE h."OperadorId" = e."OperadorId"
                    AND (h."Id" = v_ruta OR EXISTS (SELECT 1 FROM public."ParadasRuta" p WHERE p."RutaId" = h."Id" AND p."EnvioId" = p_envio AND p."Activa"));
                UPDATE public."ParadasRuta" SET "Activa" = false, "MotivoSuspension" = btrim(p_motivo)
                    WHERE "EnvioId" = p_envio AND "OperadorId" = e."OperadorId" AND "Activa";
                UPDATE public."Envios" SET "Estado" = 'Cancelado', "Version" = p_version + 1 WHERE "Id" = p_envio;
                INSERT INTO public."CancelacionesEnvio" ("Id","OperadorId","EnvioId","AccesoId","UsuarioId","TipoActor","OrganizacionActorId",
                    "Motivo","EstadoAnterior","VersionAnterior","VersionEnvio","CreadaEnUtc")
                    VALUES (p_id,e."OperadorId",p_envio,v_acceso,v_usuario,CASE WHEN v_actor = 'operador' THEN 'Operador' ELSE 'Comercio' END,
                        v_org,btrim(p_motivo),e."Estado",p_version,p_version+1,v_ahora);
                IF v_ruta IS NOT NULL THEN
                    INSERT INTO public."AvisosReparto" ("Id","OperadorId","AccesoId","RutaId","EnvioId","VersionEnvio","Tipo","Motivo","CreadoEnUtc")
                        VALUES(p_aviso,e."OperadorId",v_repartidor,v_ruta,p_envio,p_version+1,'Cancelacion',btrim(p_motivo),v_ahora);
                    INSERT INTO public."OutboxMessages" ("Id","OperadorId","EnvioId","Ambito","ClaveAmbito","Tipo","VersionContrato","Destino",
                        "Prioridad","Estado","Payload","Intentos","CreadoEnUtc","ProximoIntentoEnUtc")
                        VALUES(p_mensaje,e."OperadorId",p_envio,'Envio',p_envio,'RepartoModificado',1,'PushFcm','Alta','Pendiente',
                            jsonb_build_object('avisoId',p_aviso),0,v_ahora,v_ahora);
                END IF;
                RETURN p_id;
            END $cancel$;
ALTER FUNCTION nexoruta_app.cancel_shipment(uuid,bigint,text,uuid,uuid,uuid) OWNER TO nexoruta_cancel_fn;
REVOKE ALL ON FUNCTION nexoruta_app.cancel_shipment(uuid,bigint,text,uuid,uuid,uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nexoruta_app.cancel_shipment(uuid,bigint,text,uuid,uuid,uuid) TO nexoruta_api;
DROP FUNCTION nexoruta_app.apply_shipment_cancellation(uuid,bigint,text,uuid,uuid,uuid,uuid);
DROP POLICY cancellation_public_shipment ON public."Envios";
DROP POLICY cancellation_public_audit ON public."CancelacionesEnvio";
DROP POLICY public_cancel_link ON public."EnlacesSeguimiento";
DROP POLICY public_cancel_envio ON public."Envios";
DROP POLICY public_cancel_recipient ON public."Destinatarios";
DROP POLICY public_cancel_operator ON public."Operadores";
DROP POLICY public_cancel_outbox ON public."OutboxMessages";
REVOKE ALL ON public."EnlacesSeguimiento" FROM nexoruta_cancel_public_fn;
REVOKE ALL ON public."Envios" FROM nexoruta_cancel_public_fn;
REVOKE ALL ON public."Destinatarios" FROM nexoruta_cancel_public_fn;
REVOKE ALL ON public."Operadores" FROM nexoruta_cancel_public_fn;
REVOKE ALL ON public."OutboxMessages" FROM nexoruta_cancel_public_fn;
REVOKE UPDATE ("Id") ON public."Operadores" FROM nexoruta_cancel_public_fn;

""");
}
