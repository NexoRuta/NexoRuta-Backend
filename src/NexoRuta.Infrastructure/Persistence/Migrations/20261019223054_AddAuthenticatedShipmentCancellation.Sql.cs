using Microsoft.EntityFrameworkCore.Migrations;

namespace NexoRuta.Infrastructure.Persistence.Migrations;

public partial class AddAuthenticatedShipmentCancellation
{
    private static void ConfigureCancellation(MigrationBuilder migration)
    {
        migration.Sql("""
            DO $role$ BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexoruta_cancel_fn') THEN
                    CREATE ROLE nexoruta_cancel_fn NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
                END IF;
            END $role$;
            DO $safe_roles$ BEGIN
                IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname IN ('nexoruta_cancel_fn')
                    AND (rolsuper OR rolbypassrls OR rolreplication)) THEN
                    RAISE EXCEPTION 'Application roles must not have SUPERUSER, BYPASSRLS or REPLICATION; repair them with an authorized administrator.';
                END IF;
            END $safe_roles$;
            ALTER ROLE nexoruta_cancel_fn NOLOGIN NOINHERIT NOCREATEDB NOCREATEROLE;
            GRANT USAGE ON SCHEMA public, nexoruta_app TO nexoruta_cancel_fn;
            GRANT EXECUTE ON FUNCTION nexoruta_app.setting(text), nexoruta_app.ctx_uuid(text) TO nexoruta_cancel_fn;
            ALTER TABLE public."CancelacionesEnvio" OWNER TO nexoruta_schema_owner;
            ALTER TABLE public."CancelacionesEnvio" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE public."CancelacionesEnvio" FORCE ROW LEVEL SECURITY;
            REVOKE ALL ON public."CancelacionesEnvio" FROM PUBLIC;
            GRANT SELECT ON public."CancelacionesEnvio" TO nexoruta_api;
            CREATE POLICY cancellation_api_select ON public."CancelacionesEnvio" FOR SELECT TO nexoruta_api
                USING ((nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'))
                    OR (nexoruta_app.setting('actor_kind') = 'comercio' AND "TipoActor" = 'Comercio'
                        AND "OrganizacionActorId" = nexoruta_app.ctx_uuid('org_id')));
            CREATE TRIGGER cancelacion_inmutable BEFORE UPDATE OR DELETE ON public."CancelacionesEnvio"
                FOR EACH ROW EXECUTE FUNCTION public."ImpedirCambioHechoReparto"();

            GRANT SELECT ON public."Envios", public."Operadores", public."AccesosUsuario", public."Roles",
                public."RolesPermisos", public."Permisos", public."Bultos", public."ParadasRuta", public."HojasRuta",
                public."CancelacionesEnvio" TO nexoruta_cancel_fn;
            GRANT UPDATE ("Estado", "Version") ON public."Envios" TO nexoruta_cancel_fn;
            GRANT UPDATE ("Id") ON public."Operadores" TO nexoruta_cancel_fn;
            GRANT UPDATE ("Activa", "MotivoSuspension") ON public."ParadasRuta" TO nexoruta_cancel_fn;
            GRANT UPDATE ("Version") ON public."HojasRuta" TO nexoruta_cancel_fn;
            GRANT INSERT ON public."CancelacionesEnvio", public."AvisosReparto", public."OutboxMessages" TO nexoruta_cancel_fn;

            CREATE POLICY cancellation_shipment ON public."Envios" TO nexoruta_cancel_fn
                USING ("Id" = nexoruta_app.ctx_uuid('cancel_shipment_id') AND
                    ((nexoruta_app.setting('actor_kind') = 'comercio' AND "ComercioId" = nexoruta_app.ctx_uuid('org_id'))
                    OR (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'))))
                WITH CHECK ("Id" = nexoruta_app.ctx_uuid('cancel_shipment_id') AND "OperadorId" = nexoruta_app.ctx_uuid('cancel_operator_id')
                    AND "Estado" = 'Cancelado' AND "EntregaFisicaEnUtc" IS NULL);
            CREATE POLICY cancellation_operator_lock ON public."Operadores" TO nexoruta_cancel_fn
                USING ("Id" = nexoruta_app.ctx_uuid('cancel_operator_id'))
                WITH CHECK (false);
            CREATE POLICY cancellation_access ON public."AccesosUsuario" FOR SELECT TO nexoruta_cancel_fn
                USING ("Id" = nexoruta_app.ctx_uuid('access_id') AND "UsuarioId" = nexoruta_app.ctx_uuid('user_id'));
            CREATE POLICY cancellation_role ON public."Roles" FOR SELECT TO nexoruta_cancel_fn
                USING (EXISTS (SELECT 1 FROM public."AccesosUsuario" a WHERE a."RolId" = "Roles"."Id"));
            CREATE POLICY cancellation_role_permission ON public."RolesPermisos" FOR SELECT TO nexoruta_cancel_fn
                USING (EXISTS (SELECT 1 FROM public."AccesosUsuario" a WHERE a."RolId" = "RolesPermisos"."RolId"));
            CREATE POLICY cancellation_permission ON public."Permisos" FOR SELECT TO nexoruta_cancel_fn USING (true);
            CREATE POLICY cancellation_parcel ON public."Bultos" FOR SELECT TO nexoruta_cancel_fn
                USING ("OperadorId" = nexoruta_app.ctx_uuid('cancel_operator_id') AND "EnvioId" = nexoruta_app.ctx_uuid('cancel_shipment_id'));
            CREATE POLICY cancellation_stop ON public."ParadasRuta" TO nexoruta_cancel_fn
                USING ("OperadorId" = nexoruta_app.ctx_uuid('cancel_operator_id') AND "EnvioId" = nexoruta_app.ctx_uuid('cancel_shipment_id'))
                WITH CHECK ("OperadorId" = nexoruta_app.ctx_uuid('cancel_operator_id') AND "EnvioId" = nexoruta_app.ctx_uuid('cancel_shipment_id'));
            CREATE POLICY cancellation_route ON public."HojasRuta" TO nexoruta_cancel_fn
                USING ("OperadorId" = nexoruta_app.ctx_uuid('cancel_operator_id') AND
                    (EXISTS (SELECT 1 FROM public."ParadasRuta" p WHERE p."RutaId" = "HojasRuta"."Id")
                        OR EXISTS (SELECT 1 FROM public."Bultos" b WHERE b."RutaCustodiaId" = "HojasRuta"."Id")))
                WITH CHECK ("OperadorId" = nexoruta_app.ctx_uuid('cancel_operator_id'));
            CREATE POLICY cancellation_audit ON public."CancelacionesEnvio" TO nexoruta_cancel_fn
                USING ("OperadorId" = nexoruta_app.ctx_uuid('cancel_operator_id') AND "EnvioId" = nexoruta_app.ctx_uuid('cancel_shipment_id'))
                WITH CHECK ("OperadorId" = nexoruta_app.ctx_uuid('cancel_operator_id') AND "EnvioId" = nexoruta_app.ctx_uuid('cancel_shipment_id')
                    AND "AccesoId" = nexoruta_app.ctx_uuid('access_id') AND "UsuarioId" = nexoruta_app.ctx_uuid('user_id'));
            CREATE POLICY cancellation_notice ON public."AvisosReparto" FOR INSERT TO nexoruta_cancel_fn
                WITH CHECK ("OperadorId" = nexoruta_app.ctx_uuid('cancel_operator_id') AND "EnvioId" = nexoruta_app.ctx_uuid('cancel_shipment_id') AND "Tipo" = 'Cancelacion');
            CREATE POLICY cancellation_outbox ON public."OutboxMessages" FOR INSERT TO nexoruta_cancel_fn
                WITH CHECK ("OperadorId" = nexoruta_app.ctx_uuid('cancel_operator_id') AND "EnvioId" = nexoruta_app.ctx_uuid('cancel_shipment_id')
                    AND "Ambito" = 'Envio' AND "ClaveAmbito" = "EnvioId" AND "Tipo" = 'RepartoModificado' AND "Destino" = 'PushFcm'
                    AND "VersionContrato" = 1 AND "Estado" = 'Pendiente' AND "Prioridad" = 'Alta' AND "Intentos" = 0);

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
            """);
    }

    private static void RemoveCancellation(MigrationBuilder migration)
    {
        migration.Sql("DROP FUNCTION nexoruta_app.cancel_shipment(uuid,bigint,text,uuid,uuid,uuid);");
        migration.Sql("""
            REVOKE UPDATE ("Estado", "Version") ON public."Envios" FROM nexoruta_cancel_fn;
            REVOKE UPDATE ("Id") ON public."Operadores" FROM nexoruta_cancel_fn;
            REVOKE UPDATE ("Activa", "MotivoSuspension") ON public."ParadasRuta" FROM nexoruta_cancel_fn;
            REVOKE UPDATE ("Version") ON public."HojasRuta" FROM nexoruta_cancel_fn;
            REVOKE EXECUTE ON FUNCTION nexoruta_app.setting(text), nexoruta_app.ctx_uuid(text) FROM nexoruta_cancel_fn;
            """);
        foreach (var (table, policy) in new[] { ("Envios", "shipment"), ("Operadores", "operator_lock"), ("AccesosUsuario", "access"),
            ("Roles", "role"), ("RolesPermisos", "role_permission"), ("Permisos", "permission"), ("Bultos", "parcel"),
            ("ParadasRuta", "stop"), ("HojasRuta", "route"), ("AvisosReparto", "notice"), ("OutboxMessages", "outbox") })
        {
            migration.Sql($"DROP POLICY cancellation_{policy} ON public.\"{table}\"; REVOKE ALL ON public.\"{table}\" FROM nexoruta_cancel_fn;");
        }
    }
}
