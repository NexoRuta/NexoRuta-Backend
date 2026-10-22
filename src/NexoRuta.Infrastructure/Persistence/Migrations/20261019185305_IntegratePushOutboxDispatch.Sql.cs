using Microsoft.EntityFrameworkCore.Migrations;
namespace NexoRuta.Infrastructure.Persistence.Migrations;

public partial class IntegratePushOutboxDispatch
{
    private static void ConfigureDispatch(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION nexoruta_app.claim_outbox(p_processor_id uuid, p_destination text)
            RETURNS TABLE("Id" uuid, "OperadorId" uuid, "EnvioId" uuid, "Ambito" text, "ClaveAmbito" uuid,
                          "Tipo" text, "VersionContrato" integer, "Destino" text, "Prioridad" text,
                          "Payload" text, "EstadoEntrega" text, "Intentos" integer, "CreadoEnUtc" timestamptz)
            LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
            AS $claim$
            DECLARE v_id uuid; v_now timestamptz := clock_timestamp();
            BEGIN
                IF p_processor_id IS NULL OR p_processor_id = '00000000-0000-0000-0000-000000000000'::uuid
                   OR p_destination NOT IN ('RabbitMq', 'Kafka', 'PushFcm') THEN RAISE EXCEPTION 'Invalid outbox claim.'; END IF;
                PERFORM set_config('nexoruta.queue_mode', 'claim', true);
                PERFORM set_config('nexoruta.queue_destination', p_destination, true);
                PERFORM set_config('nexoruta.queue_processor_id', p_processor_id::text, true);
                SELECT m."Id" INTO v_id FROM public."OutboxMessages" m
                 WHERE m."Destino" = p_destination AND (
                     (m."Estado" IN ('Pendiente', 'Error') AND m."ProximoIntentoEnUtc" <= v_now) OR
                     (m."Estado" = 'Procesando' AND m."ReservaHastaUtc" <= v_now))
                   AND (p_destination <> 'Kafka' OR NOT EXISTS (
                       SELECT 1 FROM public."OutboxMessages" older
                       WHERE older."Destino" = 'Kafka' AND older."Ambito" = m."Ambito"
                         AND older."ClaveAmbito" = m."ClaveAmbito"
                         AND (older."CreadoEnUtc", older."Id") < (m."CreadoEnUtc", m."Id")
                         AND older."Estado" <> 'Publicado'))
                 ORDER BY LEAST(2, CASE m."Prioridad" WHEN 'Alta' THEN 2 WHEN 'Normal' THEN 1 ELSE 0 END +
                     floor(extract(epoch FROM (v_now - m."CreadoEnUtc")) / 300)::integer) DESC,
                     m."CreadoEnUtc", m."Id" FOR UPDATE SKIP LOCKED LIMIT 1;
                IF v_id IS NULL THEN RETURN; END IF;
                PERFORM set_config('nexoruta.queue_message_id', v_id::text, true);
                RETURN QUERY
                UPDATE public."OutboxMessages" m SET "Estado" = 'Procesando', "ProcesadorId" = p_processor_id,
                       "ReservaHastaUtc" = v_now + interval '2 minutes',
                       "Intentos" = CASE WHEN m."Intentos" < 2147483647 THEN m."Intentos" + 1 ELSE m."Intentos" END
                 WHERE m."Id" = v_id
                RETURNING m."Id", m."OperadorId", m."EnvioId", m."Ambito"::text, m."ClaveAmbito",
                          m."Tipo"::text, m."VersionContrato", m."Destino"::text, m."Prioridad"::text,
                          m."Payload"::text, m."EstadoEntrega"::text, m."Intentos", m."CreadoEnUtc";
            END
            $claim$;
            """);
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION nexoruta_app.finish_outbox(
                p_message_id uuid, p_processor_id uuid, p_success boolean, p_error text DEFAULT NULL)
            RETURNS boolean
            LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
            AS $finish$
            DECLARE v_now timestamptz := clock_timestamp();
            BEGIN
                IF p_message_id IS NULL OR p_processor_id IS NULL THEN RETURN false; END IF;
                PERFORM set_config('nexoruta.queue_mode', 'finish', true);
                PERFORM set_config('nexoruta.queue_message_id', p_message_id::text, true);
                PERFORM set_config('nexoruta.queue_processor_id', p_processor_id::text, true);
                UPDATE public."OutboxMessages" m SET
                    "Estado" = CASE WHEN p_success THEN 'Publicado' ELSE 'Error' END,
                    "PublicadoEnUtc" = CASE WHEN p_success THEN v_now ELSE m."PublicadoEnUtc" END,
                    "ProximoIntentoEnUtc" = CASE WHEN p_success THEN m."ProximoIntentoEnUtc" WHEN m."Destino" = 'PushFcm' THEN v_now + make_interval(secs =>
                        LEAST(3600::double precision, 5 * power(2::double precision, LEAST(m."Intentos", 10))) + floor(random() * 15))
                        ELSE v_now + interval '30 seconds' END,
                    "UltimoError" = CASE WHEN p_success THEN NULL ELSE left(COALESCE(NULLIF(btrim(p_error), ''), 'Error de publicación'), 2000) END,
                    "ProcesadorId" = NULL, "ReservaHastaUtc" = NULL
                WHERE m."Id" = p_message_id AND m."Estado" = 'Procesando'
                  AND m."ProcesadorId" = p_processor_id AND m."ReservaHastaUtc" > v_now;
                RETURN FOUND;
            END
            $finish$;
            """);
        migrationBuilder.Sql("""
            GRANT SELECT ON public."AvisosReparto", public."HojasRuta", public."Envios", public."InstalacionesMoviles",
                public."SesionesMoviles", public."AccesosUsuario", public."Repartidores", public."Operadores",
                public."RolesPermisos", public."Permisos", public."EntregasPush" TO nexoruta_queue_fn;
            GRANT SELECT ("Id", "VersionCredencial") ON public."Usuarios" TO nexoruta_queue_fn;
            GRANT INSERT ON public."EntregasPush" TO nexoruta_queue_fn;
            GRANT UPDATE ("Activa") ON public."InstalacionesMoviles" TO nexoruta_queue_fn;

            CREATE POLICY push_job_outbox_insert ON public."OutboxMessages" FOR INSERT TO nexoruta_api
            WITH CHECK (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id')
                AND "Ambito" = 'Envio' AND "ClaveAmbito" = "EnvioId" AND "Tipo" = 'RepartoModificado'
                AND "VersionContrato" = 1 AND "Destino" = 'PushFcm' AND "Prioridad" = 'Alta'
                AND "Estado" = 'Pendiente' AND "Intentos" = 0 AND "ProcesadorId" IS NULL AND "ReservaHastaUtc" IS NULL
                AND "PublicadoEnUtc" IS NULL AND "UltimoError" IS NULL AND "EstadoEntrega" IS NULL
                AND EXISTS (SELECT 1 FROM public."AvisosReparto" a WHERE a."Id"::text = "OutboxMessages"."Payload"->>'avisoId'
                    AND a."OperadorId" = "OutboxMessages"."OperadorId" AND a."EnvioId" = "OutboxMessages"."EnvioId"));

            CREATE POLICY push_fn_notice ON public."AvisosReparto" FOR SELECT TO nexoruta_queue_fn
                USING ("OperadorId" = nexoruta_app.ctx_uuid('push_op_id') AND "Id" = nexoruta_app.ctx_uuid('push_aviso_id'));
            CREATE POLICY push_fn_route ON public."HojasRuta" FOR SELECT TO nexoruta_queue_fn
                USING ("OperadorId" = nexoruta_app.ctx_uuid('push_op_id') AND "Id" = nexoruta_app.ctx_uuid('push_ruta_id'));
            CREATE POLICY push_fn_shipment ON public."Envios" FOR SELECT TO nexoruta_queue_fn
                USING ("OperadorId" = nexoruta_app.ctx_uuid('push_op_id') AND "Id" = nexoruta_app.ctx_uuid('push_envio_id'));
            CREATE POLICY push_fn_installation ON public."InstalacionesMoviles" FOR SELECT TO nexoruta_queue_fn
                USING ("OperadorId" = nexoruta_app.ctx_uuid('push_op_id') AND "AccesoId" = nexoruta_app.ctx_uuid('push_acceso_id'));
            CREATE POLICY push_fn_disable_installation ON public."InstalacionesMoviles" FOR UPDATE TO nexoruta_queue_fn
                USING ("OperadorId" = nexoruta_app.ctx_uuid('push_op_id') AND "AccesoId" = nexoruta_app.ctx_uuid('push_acceso_id'))
                WITH CHECK ("OperadorId" = nexoruta_app.ctx_uuid('push_op_id') AND "AccesoId" = nexoruta_app.ctx_uuid('push_acceso_id'));
            CREATE POLICY push_fn_receipt ON public."EntregasPush" FOR SELECT TO nexoruta_queue_fn
                USING ("OperadorId" = nexoruta_app.ctx_uuid('push_op_id') AND "AvisoId" = nexoruta_app.ctx_uuid('push_aviso_id'));
            CREATE POLICY push_fn_insert_receipt ON public."EntregasPush" FOR INSERT TO nexoruta_queue_fn
                WITH CHECK ("OperadorId" = nexoruta_app.ctx_uuid('push_op_id') AND "AvisoId" = nexoruta_app.ctx_uuid('push_aviso_id'));
            CREATE POLICY push_fn_access ON public."AccesosUsuario" FOR SELECT TO nexoruta_queue_fn
                USING ("OperadorId" = nexoruta_app.ctx_uuid('push_op_id') AND "Id" = nexoruta_app.ctx_uuid('push_acceso_id'));
            CREATE POLICY push_fn_courier ON public."Repartidores" FOR SELECT TO nexoruta_queue_fn
                USING ("OperadorId" = nexoruta_app.ctx_uuid('push_op_id') AND "AccesoId" = nexoruta_app.ctx_uuid('push_acceso_id'));
            CREATE POLICY push_fn_operator ON public."Operadores" FOR SELECT TO nexoruta_queue_fn
                USING ("Id" = nexoruta_app.ctx_uuid('push_op_id'));
            CREATE POLICY push_fn_permission ON public."Permisos" FOR SELECT TO nexoruta_queue_fn USING ("Codigo" = 'reparto.ejecutar');
            CREATE POLICY push_fn_grants ON public."RolesPermisos" FOR SELECT TO nexoruta_queue_fn
                USING (EXISTS (SELECT 1 FROM public."AccesosUsuario" a WHERE a."Id" = nexoruta_app.ctx_uuid('push_acceso_id') AND a."RolId" = "RolesPermisos"."RolId"));

            CREATE OR REPLACE FUNCTION nexoruta_app.bind_push_job(p_message uuid, p_processor uuid)
            RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
            AS $bind$
            DECLARE m record; a record;
            BEGIN
                PERFORM set_config('nexoruta.queue_mode', 'finish', true),
                    set_config('nexoruta.queue_message_id', COALESCE(p_message::text, ''), true),
                    set_config('nexoruta.queue_processor_id', COALESCE(p_processor::text, ''), true);
                SELECT * INTO m FROM public."OutboxMessages" o
                    WHERE o."Id" = p_message AND o."ProcesadorId" = p_processor AND o."Estado" = 'Procesando'
                        AND o."ReservaHastaUtc" > clock_timestamp() AND o."Destino" = 'PushFcm' AND o."Ambito" = 'Envio'
                        AND o."ClaveAmbito" = o."EnvioId" AND o."Tipo" = 'RepartoModificado' AND o."VersionContrato" = 1
                    FOR UPDATE;
                IF NOT FOUND THEN RAISE EXCEPTION 'Push claim is unavailable.' USING ERRCODE = '42501'; END IF;
                PERFORM set_config('nexoruta.push_op_id', m."OperadorId"::text, true),
                    set_config('nexoruta.push_envio_id', m."EnvioId"::text, true),
                    set_config('nexoruta.push_aviso_id', (m."Payload"->>'avisoId')::uuid::text, true);
                SELECT * INTO a FROM public."AvisosReparto" n WHERE n."Id" = nexoruta_app.ctx_uuid('push_aviso_id')
                    AND n."OperadorId" = m."OperadorId" AND n."EnvioId" = m."EnvioId";
                IF NOT FOUND THEN RAISE EXCEPTION 'Push notice does not match its message.' USING ERRCODE = '22023'; END IF;
                PERFORM set_config('nexoruta.push_acceso_id', a."AccesoId"::text, true),
                    set_config('nexoruta.push_ruta_id', a."RutaId"::text, true);
                IF NOT EXISTS (SELECT 1 FROM public."HojasRuta" h WHERE h."Id" = a."RutaId" AND h."RepartidorAccesoId" = a."AccesoId") THEN
                    RAISE EXCEPTION 'Push recipient does not own the route.' USING ERRCODE = '22023';
                END IF;
                RETURN jsonb_build_object('Id', a."Id", 'OperadorId', a."OperadorId", 'AccesoId', a."AccesoId", 'EnvioId', a."EnvioId");
            END
            $bind$;

            CREATE OR REPLACE FUNCTION nexoruta_app.prepare_push_dispatch(p_message uuid, p_processor uuid)
            RETURNS text LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
            AS $prepare$
            DECLARE a jsonb; devices jsonb := '[]'::jsonb;
            BEGIN
                a := nexoruta_app.bind_push_job(p_message, p_processor);
                IF EXISTS (SELECT 1 FROM public."AvisosReparto" n
                    JOIN public."HojasRuta" h ON h."Id" = n."RutaId" AND h."OperadorId" = n."OperadorId"
                    JOIN public."Envios" e ON e."Id" = n."EnvioId" AND e."OperadorId" = n."OperadorId"
                WHERE n."ConfirmadoEnUtc" IS NULL AND h."Estado" <> 'Finalizada' AND e."EntregaFisicaEnUtc" IS NULL AND n."VersionEnvio" = e."Version") THEN
                    SELECT COALESCE(jsonb_agg(d.payload), '[]'::jsonb) INTO devices FROM (
                        SELECT DISTINCT ON (i."FidHash") jsonb_build_object(
                            'Id', i."Id", 'OperadorId', i."OperadorId", 'AccesoId', i."AccesoId", 'FidHash', i."FidHash",
                            'FidCifrado', replace(encode(i."FidCifrado", 'base64'), chr(10), ''),
                            'Nonce', encode(i."Nonce", 'base64'), 'Tag', encode(i."Tag", 'base64')) AS payload
                        FROM public."InstalacionesMoviles" i
                        JOIN public."SesionesMoviles" s ON s."Id" = i."SesionId" AND s."AccesoId" = i."AccesoId"
                        JOIN public."AccesosUsuario" ac ON ac."Id" = i."AccesoId" AND ac."OperadorId" = i."OperadorId"
                        JOIN public."Usuarios" u ON u."Id" = s."UsuarioId" AND u."Id" = ac."UsuarioId"
                        JOIN public."Repartidores" r ON r."AccesoId" = i."AccesoId" AND r."OperadorId" = i."OperadorId"
                        JOIN public."Operadores" o ON o."Id" = i."OperadorId"
                        WHERE i."Activa" AND i."Permitida" AND ac."Activo" AND r."Activo" AND o."Estado" = 'Activa'
                            AND s."RevocadaEnUtc" IS NULL AND s."ExpiraEnUtc" > clock_timestamp()
                            AND s."VersionSesion" = ac."VersionSesion" AND s."VersionCredencial" = u."VersionCredencial"
                            AND EXISTS (SELECT 1 FROM public."RolesPermisos" rp JOIN public."Permisos" p ON p."Id" = rp."PermisoId"
                                WHERE rp."RolId" = ac."RolId" AND p."Codigo" = 'reparto.ejecutar')
                            AND NOT EXISTS (SELECT 1 FROM public."EntregasPush" ep
                                WHERE ep."AvisoId" = nexoruta_app.ctx_uuid('push_aviso_id') AND ep."FidHash" = i."FidHash")
                        ORDER BY i."FidHash", i."ActualizadaEnUtc" DESC, i."Id"
                        LIMIT 100
                    ) d;
                END IF;
                RETURN jsonb_build_object('Aviso', a, 'Instalaciones', devices)::text;
            END
            $prepare$;

            CREATE OR REPLACE FUNCTION nexoruta_app.record_push_result(
                p_message uuid, p_processor uuid, p_installation uuid, p_hash text, p_result text, p_provider text)
            RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
            AS $record$
            DECLARE a jsonb;
            BEGIN
                a := nexoruta_app.bind_push_job(p_message, p_processor);
                IF p_hash IS NULL OR p_hash !~ '^[0-9A-F]{64}$' OR p_result IS NULL OR p_result NOT IN ('FcmAceptada', 'FidInvalido')
                    OR length(p_provider) > 500 OR (p_result = 'FcmAceptada' AND COALESCE(length(btrim(p_provider)), 0) = 0) THEN
                    RAISE EXCEPTION 'Invalid push result.' USING ERRCODE = '22023';
                END IF;
                IF NOT EXISTS (SELECT 1 FROM public."InstalacionesMoviles" i WHERE i."Id" = p_installation) THEN RETURN false; END IF;
                INSERT INTO public."EntregasPush" ("OperadorId", "AvisoId", "InstalacionId", "FidHash", "Resultado", "IdFcm", "RegistradaEnUtc")
                VALUES ((a->>'OperadorId')::uuid, (a->>'Id')::uuid, p_installation, p_hash, p_result, p_provider, clock_timestamp())
                ON CONFLICT ("AvisoId", "InstalacionId", "FidHash") DO NOTHING;
                IF p_result = 'FidInvalido' THEN
                    UPDATE public."InstalacionesMoviles" SET "Activa" = false WHERE "Id" = p_installation AND "FidHash" = p_hash;
                END IF;
                RETURN true;
            END
            $record$;

            ALTER FUNCTION nexoruta_app.bind_push_job(uuid, uuid) OWNER TO nexoruta_queue_fn;
            ALTER FUNCTION nexoruta_app.prepare_push_dispatch(uuid, uuid) OWNER TO nexoruta_queue_fn;
            ALTER FUNCTION nexoruta_app.record_push_result(uuid, uuid, uuid, text, text, text) OWNER TO nexoruta_queue_fn;
            REVOKE ALL ON FUNCTION nexoruta_app.bind_push_job(uuid, uuid),
                nexoruta_app.prepare_push_dispatch(uuid, uuid), nexoruta_app.record_push_result(uuid, uuid, uuid, text, text, text) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION nexoruta_app.prepare_push_dispatch(uuid, uuid),
                nexoruta_app.record_push_result(uuid, uuid, uuid, text, text, text) TO nexoruta_worker;
            """);
    }

    private static void RemoveDispatch(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP FUNCTION nexoruta_app.record_push_result(uuid, uuid, uuid, text, text, text);
            DROP FUNCTION nexoruta_app.prepare_push_dispatch(uuid, uuid);
            DROP FUNCTION nexoruta_app.bind_push_job(uuid, uuid);
            DROP POLICY push_fn_notice ON public."AvisosReparto";
            DROP POLICY push_fn_route ON public."HojasRuta";
            DROP POLICY push_fn_shipment ON public."Envios";
            DROP POLICY push_fn_installation ON public."InstalacionesMoviles";
            DROP POLICY push_fn_disable_installation ON public."InstalacionesMoviles";
            DROP POLICY push_fn_receipt ON public."EntregasPush";
            DROP POLICY push_fn_insert_receipt ON public."EntregasPush";
            DROP POLICY push_fn_access ON public."AccesosUsuario";
            DROP POLICY push_fn_courier ON public."Repartidores";
            DROP POLICY push_fn_operator ON public."Operadores";
            DROP POLICY push_fn_permission ON public."Permisos";
            DROP POLICY push_fn_grants ON public."RolesPermisos";
            DROP POLICY push_job_outbox_insert ON public."OutboxMessages";
            REVOKE SELECT ON public."AvisosReparto", public."HojasRuta", public."Envios", public."InstalacionesMoviles",
                public."SesionesMoviles", public."AccesosUsuario", public."Repartidores", public."Operadores",
                public."RolesPermisos", public."Permisos", public."EntregasPush" FROM nexoruta_queue_fn;
            REVOKE SELECT ("Id", "VersionCredencial") ON public."Usuarios" FROM nexoruta_queue_fn;
            REVOKE INSERT ON public."EntregasPush" FROM nexoruta_queue_fn;
            REVOKE UPDATE ("Activa") ON public."InstalacionesMoviles" FROM nexoruta_queue_fn;
            """);
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION nexoruta_app.claim_outbox(p_processor_id uuid, p_destination text)
            RETURNS TABLE("Id" uuid, "OperadorId" uuid, "EnvioId" uuid, "Ambito" text, "ClaveAmbito" uuid,
                          "Tipo" text, "VersionContrato" integer, "Destino" text, "Prioridad" text,
                          "Payload" text, "EstadoEntrega" text, "Intentos" integer, "CreadoEnUtc" timestamptz)
            LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
            AS $claim$
            DECLARE v_id uuid; v_now timestamptz := clock_timestamp();
            BEGIN
                IF p_processor_id IS NULL OR p_processor_id = '00000000-0000-0000-0000-000000000000'::uuid
                   OR p_destination NOT IN ('RabbitMq', 'Kafka') THEN RAISE EXCEPTION 'Invalid outbox claim.'; END IF;
                PERFORM set_config('nexoruta.queue_mode', 'claim', true);
                PERFORM set_config('nexoruta.queue_destination', p_destination, true);
                PERFORM set_config('nexoruta.queue_processor_id', p_processor_id::text, true);
                SELECT m."Id" INTO v_id FROM public."OutboxMessages" m
                 WHERE m."Destino" = p_destination AND (
                     (m."Estado" IN ('Pendiente', 'Error') AND m."ProximoIntentoEnUtc" <= v_now) OR
                     (m."Estado" = 'Procesando' AND m."ReservaHastaUtc" <= v_now))
                   AND (p_destination <> 'Kafka' OR NOT EXISTS (
                       SELECT 1 FROM public."OutboxMessages" older
                       WHERE older."Destino" = 'Kafka' AND older."Ambito" = m."Ambito"
                         AND older."ClaveAmbito" = m."ClaveAmbito"
                         AND (older."CreadoEnUtc", older."Id") < (m."CreadoEnUtc", m."Id")
                         AND older."Estado" <> 'Publicado'))
                 ORDER BY LEAST(2, CASE m."Prioridad" WHEN 'Alta' THEN 2 WHEN 'Normal' THEN 1 ELSE 0 END +
                     floor(extract(epoch FROM (v_now - m."CreadoEnUtc")) / 300)::integer) DESC,
                     m."CreadoEnUtc", m."Id" FOR UPDATE SKIP LOCKED LIMIT 1;
                IF v_id IS NULL THEN RETURN; END IF;
                PERFORM set_config('nexoruta.queue_message_id', v_id::text, true);
                RETURN QUERY
                UPDATE public."OutboxMessages" m SET "Estado" = 'Procesando', "ProcesadorId" = p_processor_id,
                       "ReservaHastaUtc" = v_now + interval '2 minutes',
                       "Intentos" = CASE WHEN m."Intentos" < 2147483647 THEN m."Intentos" + 1 ELSE m."Intentos" END
                 WHERE m."Id" = v_id
                RETURNING m."Id", m."OperadorId", m."EnvioId", m."Ambito"::text, m."ClaveAmbito",
                          m."Tipo"::text, m."VersionContrato", m."Destino"::text, m."Prioridad"::text,
                          m."Payload"::text, m."EstadoEntrega"::text, m."Intentos", m."CreadoEnUtc";
            END
            $claim$;
            """);
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION nexoruta_app.finish_outbox(
                p_message_id uuid, p_processor_id uuid, p_success boolean, p_error text DEFAULT NULL)
            RETURNS boolean
            LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
            AS $finish$
            DECLARE v_now timestamptz := clock_timestamp();
            BEGIN
                IF p_message_id IS NULL OR p_processor_id IS NULL THEN RETURN false; END IF;
                PERFORM set_config('nexoruta.queue_mode', 'finish', true);
                PERFORM set_config('nexoruta.queue_message_id', p_message_id::text, true);
                PERFORM set_config('nexoruta.queue_processor_id', p_processor_id::text, true);
                UPDATE public."OutboxMessages" m SET
                    "Estado" = CASE WHEN p_success THEN 'Publicado' ELSE 'Error' END,
                    "PublicadoEnUtc" = CASE WHEN p_success THEN v_now ELSE m."PublicadoEnUtc" END,
                    "ProximoIntentoEnUtc" = CASE WHEN p_success THEN m."ProximoIntentoEnUtc" ELSE v_now + interval '30 seconds' END,
                    "UltimoError" = CASE WHEN p_success THEN NULL ELSE left(COALESCE(NULLIF(btrim(p_error), ''), 'Error de publicación'), 2000) END,
                    "ProcesadorId" = NULL, "ReservaHastaUtc" = NULL
                WHERE m."Id" = p_message_id AND m."Estado" = 'Procesando'
                  AND m."ProcesadorId" = p_processor_id AND m."ReservaHastaUtc" > v_now;
                RETURN FOUND;
            END
            $finish$;
            """);
    }
}
