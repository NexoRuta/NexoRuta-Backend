using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnablePostgreSqlRls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pgcrypto;");
            migrationBuilder.Sql("""
                DO $roles$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexoruta_schema_owner') THEN CREATE ROLE nexoruta_schema_owner NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS; END IF;
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexoruta_api') THEN CREATE ROLE nexoruta_api NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS; END IF;
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexoruta_worker') THEN CREATE ROLE nexoruta_worker NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS; END IF;
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexoruta_bootstrap') THEN CREATE ROLE nexoruta_bootstrap NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS; END IF;
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexoruta_auth_fn') THEN CREATE ROLE nexoruta_auth_fn NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS; END IF;
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexoruta_public_fn') THEN CREATE ROLE nexoruta_public_fn NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS; END IF;
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexoruta_queue_fn') THEN CREATE ROLE nexoruta_queue_fn NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS; END IF;
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexoruta_role_fn') THEN CREATE ROLE nexoruta_role_fn NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS; END IF;
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexoruta_org_fn') THEN CREATE ROLE nexoruta_org_fn NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS; END IF;
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexoruta_shipment_fn') THEN CREATE ROLE nexoruta_shipment_fn NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS; END IF;
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexoruta_job_fn') THEN CREATE ROLE nexoruta_job_fn NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS; END IF;
                END
                $roles$;

                ALTER ROLE nexoruta_schema_owner NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
                ALTER ROLE nexoruta_api NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
                ALTER ROLE nexoruta_worker NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
                ALTER ROLE nexoruta_bootstrap NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
                ALTER ROLE nexoruta_auth_fn NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
                ALTER ROLE nexoruta_public_fn NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
                ALTER ROLE nexoruta_queue_fn NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
                ALTER ROLE nexoruta_role_fn NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
                ALTER ROLE nexoruta_org_fn NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
                ALTER ROLE nexoruta_shipment_fn NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
                ALTER ROLE nexoruta_job_fn NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
                DO $memberships$
                DECLARE membership record;
                BEGIN
                    FOR membership IN
                        SELECT member_role.rolname AS member_name, granted_role.rolname AS granted_name
                        FROM pg_auth_members m
                        JOIN pg_roles member_role ON member_role.oid = m.member
                        JOIN pg_roles granted_role ON granted_role.oid = m.roleid
                        WHERE member_role.rolname IN ('nexoruta_schema_owner','nexoruta_api','nexoruta_worker',
                            'nexoruta_bootstrap','nexoruta_auth_fn','nexoruta_public_fn','nexoruta_queue_fn',
                            'nexoruta_role_fn','nexoruta_org_fn','nexoruta_shipment_fn','nexoruta_job_fn')
                    LOOP
                        EXECUTE format('REVOKE %I FROM %I', membership.granted_name, membership.member_name);
                    END LOOP;
                END
                $memberships$;

                CREATE SCHEMA IF NOT EXISTS nexoruta_app AUTHORIZATION nexoruta_schema_owner;
                GRANT USAGE ON SCHEMA nexoruta_app TO nexoruta_api, nexoruta_worker, nexoruta_bootstrap,
                    nexoruta_auth_fn, nexoruta_public_fn, nexoruta_queue_fn, nexoruta_role_fn, nexoruta_org_fn, nexoruta_shipment_fn, nexoruta_job_fn;

                CREATE OR REPLACE FUNCTION nexoruta_app.setting(p_name text)
                RETURNS text LANGUAGE sql VOLATILE SET search_path = pg_catalog
                AS $$ SELECT NULLIF(current_setting('nexoruta.' || p_name, true), '') $$;

                CREATE OR REPLACE FUNCTION nexoruta_app.ctx_uuid(p_name text)
                RETURNS uuid LANGUAGE plpgsql VOLATILE SET search_path = pg_catalog
                AS $$
                BEGIN
                    RETURN NULLIF(current_setting('nexoruta.' || p_name, true), '')::uuid;
                EXCEPTION WHEN invalid_text_representation THEN
                    RETURN NULL;
                END
                $$;

                CREATE OR REPLACE FUNCTION nexoruta_app.ctx_bool(p_name text)
                RETURNS boolean LANGUAGE plpgsql VOLATILE SET search_path = pg_catalog
                AS $$
                BEGIN
                    RETURN COALESCE(NULLIF(current_setting('nexoruta.' || p_name, true), '')::boolean, false);
                EXCEPTION WHEN invalid_text_representation THEN
                    RETURN false;
                END
                $$;

                -- Move every table out of the bootstrap superuser's ownership before enabling RLS.
                ALTER TABLE public."AccesosPlataforma" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."RolesPlataforma" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."RolesPlataformaPermisos" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."Operadores" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."Comercios" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."Usuarios" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."AccesosUsuario" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."SolicitudesBajaOrganizacion" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."SolicitudesReactivacionOrganizacion" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."CambiosEstadoOrganizacion" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."Destinatarios" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."Direcciones" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."Envios" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."Bultos" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."EnlacesSeguimiento" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."OutboxMessages" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."ProcessedMessages" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."EntregasSeguimiento" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."Roles" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."Permisos" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."RolesPermisos" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."AspNetUserClaims" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."AspNetUserLogins" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."AspNetUserTokens" OWNER TO nexoruta_schema_owner;

                -- Identity's global account store is intentionally not tenant RLS: login/email uniqueness
                -- precedes organization selection and Identity's standard EF store owns these operations.
                REVOKE ALL ON public."Usuarios", public."AspNetUserClaims", public."AspNetUserLogins", public."AspNetUserTokens" FROM PUBLIC;

                -- Every non-Identity table in the current model is protected. Permisos is a global,
                -- non-personal catalog; its RLS policy permits a fixed catalog, not cross-tenant roles.
                ALTER TABLE public."AccesosPlataforma" ENABLE ROW LEVEL SECURITY; ALTER TABLE public."AccesosPlataforma" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."RolesPlataforma" ENABLE ROW LEVEL SECURITY; ALTER TABLE public."RolesPlataforma" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."RolesPlataformaPermisos" ENABLE ROW LEVEL SECURITY; ALTER TABLE public."RolesPlataformaPermisos" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."Operadores" ENABLE ROW LEVEL SECURITY; ALTER TABLE public."Operadores" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."Comercios" ENABLE ROW LEVEL SECURITY; ALTER TABLE public."Comercios" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."AccesosUsuario" ENABLE ROW LEVEL SECURITY; ALTER TABLE public."AccesosUsuario" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."SolicitudesBajaOrganizacion" ENABLE ROW LEVEL SECURITY; ALTER TABLE public."SolicitudesBajaOrganizacion" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."SolicitudesReactivacionOrganizacion" ENABLE ROW LEVEL SECURITY; ALTER TABLE public."SolicitudesReactivacionOrganizacion" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."CambiosEstadoOrganizacion" ENABLE ROW LEVEL SECURITY; ALTER TABLE public."CambiosEstadoOrganizacion" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."Destinatarios" ENABLE ROW LEVEL SECURITY; ALTER TABLE public."Destinatarios" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."Direcciones" ENABLE ROW LEVEL SECURITY; ALTER TABLE public."Direcciones" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."Envios" ENABLE ROW LEVEL SECURITY; ALTER TABLE public."Envios" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."Bultos" ENABLE ROW LEVEL SECURITY; ALTER TABLE public."Bultos" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."EnlacesSeguimiento" ENABLE ROW LEVEL SECURITY; ALTER TABLE public."EnlacesSeguimiento" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."EntregasSeguimiento" ENABLE ROW LEVEL SECURITY; ALTER TABLE public."EntregasSeguimiento" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."OutboxMessages" ENABLE ROW LEVEL SECURITY; ALTER TABLE public."OutboxMessages" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."ProcessedMessages" ENABLE ROW LEVEL SECURITY; ALTER TABLE public."ProcessedMessages" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."Roles" ENABLE ROW LEVEL SECURITY; ALTER TABLE public."Roles" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."Permisos" ENABLE ROW LEVEL SECURITY; ALTER TABLE public."Permisos" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."RolesPermisos" ENABLE ROW LEVEL SECURITY; ALTER TABLE public."RolesPermisos" FORCE ROW LEVEL SECURITY;

                CREATE OR REPLACE FUNCTION nexoruta_app.login_lookup(p_email text)
                RETURNS TABLE("UsuarioId" uuid, "AccesoId" uuid, "Email" text, "VersionSesion" bigint,
                              "EsPlataforma" boolean, "TipoOrganizacion" text, "EstadoOrganizacion" text)
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
                AS $login$
                DECLARE v_user_id uuid; v_email text; v_access_id uuid; v_org_id uuid; v_role_id uuid;
                        v_type text; v_state text; v_version bigint;
                BEGIN
                    SELECT u."Id", u."Email" INTO v_user_id, v_email
                      FROM public."Usuarios" u
                     WHERE lower(btrim(u."Email")) = lower(btrim(p_email))
                       AND COALESCE(u."PasswordHash", '') <> '';
                    IF v_user_id IS NULL THEN RETURN; END IF;
                    PERFORM set_config('nexoruta.auth_user_id', v_user_id::text, true);
                    SELECT a."Id", a."OrganizacionId", a."RolId", a."TipoOrganizacion", a."VersionSesion"
                      INTO v_access_id, v_org_id, v_role_id, v_type, v_version
                      FROM public."AccesosUsuario" a
                     WHERE a."UsuarioId" = v_user_id AND a."Activo" AND a."RolId" IS NOT NULL
                     ORDER BY a."Id" LIMIT 1;
                    IF v_access_id IS NOT NULL THEN
                        PERFORM set_config('nexoruta.auth_access_id', v_access_id::text, true);
                        PERFORM set_config('nexoruta.auth_org_id', v_org_id::text, true);
                        PERFORM set_config('nexoruta.auth_role_id', v_role_id::text, true);
                        IF v_type = 'Comercio' THEN
                            SELECT c."Estado" INTO v_state FROM public."Comercios" c WHERE c."Id" = v_org_id;
                        ELSE
                            SELECT o."Estado" INTO v_state FROM public."Operadores" o WHERE o."Id" = v_org_id;
                        END IF;
                        RETURN QUERY SELECT v_user_id, v_access_id, v_email, v_version, false, v_type, v_state;
                        RETURN;
                    END IF;
                    SELECT a."AccesoId", a."RolId", a."VersionSesion"
                      INTO v_access_id, v_role_id, v_version
                      FROM public."AccesosPlataforma" a
                     WHERE a."UsuarioId" = v_user_id AND a."Activo";
                    IF v_access_id IS NOT NULL THEN
                        PERFORM set_config('nexoruta.auth_access_id', v_access_id::text, true);
                        PERFORM set_config('nexoruta.auth_role_id', v_role_id::text, true);
                        RETURN QUERY SELECT v_user_id, v_access_id, v_email, v_version, true, NULL::text, NULL::text;
                        RETURN;
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM public."AccesosUsuario" a WHERE a."UsuarioId" = v_user_id)
                       AND NOT EXISTS (SELECT 1 FROM public."AccesosPlataforma" a WHERE a."UsuarioId" = v_user_id) THEN
                        RETURN QUERY SELECT v_user_id, NULL::uuid, v_email, 0::bigint, false, NULL::text, NULL::text;
                    END IF;
                END
                $login$;

                CREATE OR REPLACE FUNCTION nexoruta_app.lookup_access(p_access_id uuid)
                RETURNS TABLE("AccesoId" uuid, "UsuarioId" uuid, "OperadorId" uuid, "ComercioId" uuid,
                              "UsuarioEmail" text, "OperadorNombre" text, "ComercioNombre" text,
                              "RolId" uuid, "RolCodigo" text, "VersionSesion" bigint, "Estado" text)
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
                AS $access$
                DECLARE v_user_id uuid; v_org_id uuid; v_role_id uuid; v_type text;
                BEGIN
                    PERFORM set_config('nexoruta.auth_access_id', p_access_id::text, true);
                    SELECT a."UsuarioId", a."OrganizacionId", a."RolId", a."TipoOrganizacion"
                      INTO v_user_id, v_org_id, v_role_id, v_type
                      FROM public."AccesosUsuario" a WHERE a."Id" = p_access_id AND a."Activo";
                    IF v_user_id IS NULL OR v_role_id IS NULL THEN RETURN; END IF;
                    PERFORM set_config('nexoruta.auth_user_id', v_user_id::text, true);
                    PERFORM set_config('nexoruta.auth_org_id', v_org_id::text, true);
                    PERFORM set_config('nexoruta.auth_role_id', v_role_id::text, true);
                    RETURN QUERY
                    SELECT a."Id", u."Id", a."OperadorId", a."ComercioId", u."Email"::text,
                           o."Nombre"::text, c."Nombre"::text, r."Id", r."Codigo"::text, a."VersionSesion",
                           (CASE WHEN a."OperadorId" IS NOT NULL THEN o."Estado" ELSE c."Estado" END)::text
                      FROM public."AccesosUsuario" a
                      JOIN public."Usuarios" u ON u."Id" = a."UsuarioId"
                      JOIN public."Roles" r ON r."Id" = a."RolId"
                      LEFT JOIN public."Operadores" o ON o."Id" = a."OperadorId"
                      LEFT JOIN public."Comercios" c ON c."Id" = a."ComercioId"
                     WHERE a."Id" = p_access_id AND a."Activo" AND r."OrganizacionId" = a."OrganizacionId"
                       AND COALESCE(u."PasswordHash", '') <> '';
                END
                $access$;

                CREATE OR REPLACE FUNCTION nexoruta_app.lookup_platform_access(p_access_id uuid)
                RETURNS TABLE("UsuarioId" uuid, "AccesoId" uuid, "VersionSesion" bigint,
                              "PuedeGestionarOrganizaciones" boolean, "UsuarioEmail" text)
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
                AS $platform$
                DECLARE v_user_id uuid; v_role_id uuid;
                BEGIN
                    PERFORM set_config('nexoruta.auth_access_id', p_access_id::text, true);
                    SELECT a."UsuarioId", a."RolId" INTO v_user_id, v_role_id
                      FROM public."AccesosPlataforma" a WHERE a."AccesoId" = p_access_id AND a."Activo";
                    IF v_user_id IS NULL THEN RETURN; END IF;
                    PERFORM set_config('nexoruta.auth_user_id', v_user_id::text, true);
                    PERFORM set_config('nexoruta.auth_role_id', v_role_id::text, true);
                    RETURN QUERY
                    SELECT u."Id", a."AccesoId", a."VersionSesion",
                           r."PuedeGestionarOrganizaciones" AND EXISTS (
                               SELECT 1 FROM public."RolesPlataformaPermisos" rp
                               JOIN public."Permisos" p ON p."Id" = rp."PermisoId"
                               WHERE rp."RolId" = r."Id" AND p."Codigo" = 'plataforma.organizaciones.gestionar'),
                           u."Email"::text
                      FROM public."AccesosPlataforma" a
                      JOIN public."Usuarios" u ON u."Id" = a."UsuarioId"
                      JOIN public."RolesPlataforma" r ON r."Id" = a."RolId"
                     WHERE a."AccesoId" = p_access_id AND a."Activo" AND COALESCE(u."PasswordHash", '') <> '';
                END
                $platform$;

                CREATE OR REPLACE FUNCTION nexoruta_app.lookup_unassigned_identity(p_user_id uuid)
                RETURNS TABLE("UsuarioId" uuid, "Email" text)
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
                AS $identity$
                BEGIN
                    PERFORM set_config('nexoruta.auth_user_id', p_user_id::text, true);
                    IF EXISTS (SELECT 1 FROM public."AccesosUsuario" a WHERE a."UsuarioId" = p_user_id)
                       OR EXISTS (SELECT 1 FROM public."AccesosPlataforma" a WHERE a."UsuarioId" = p_user_id) THEN RETURN; END IF;
                    RETURN QUERY SELECT u."Id", u."Email"::text FROM public."Usuarios" u
                      WHERE u."Id" = p_user_id AND COALESCE(u."PasswordHash", '') <> '';
                END
                $identity$;

                CREATE OR REPLACE FUNCTION nexoruta_app.public_tracking(p_token text)
                RETURNS TABLE("Estado" text)
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
                AS $tracking$
                DECLARE v_hash text;
                BEGIN
                    IF p_token IS NULL OR length(p_token) <> 64 OR p_token !~ '^[0-9a-f]{64}$' THEN RETURN; END IF;
                    v_hash := encode(public.digest(convert_to(p_token, 'SQL_ASCII'), 'sha256'), 'hex');
                    PERFORM set_config('nexoruta.public_token_hash', v_hash, true);
                    RETURN QUERY
                    SELECT e."Estado"::text FROM public."Envios" e
                    WHERE EXISTS (SELECT 1 FROM public."EnlacesSeguimiento" l
                        WHERE l."EnvioId" = e."Id" AND l."OperadorId" = e."OperadorId"
                          AND l."TokenHash" = v_hash AND l."RevocadoEnUtc" IS NULL
                          AND l."ExpiraEnUtc" > now());
                END
                $tracking$;

                CREATE OR REPLACE FUNCTION nexoruta_app.create_shipment(
                    p_shipment_id uuid, p_recipient_id uuid, p_address_id uuid, p_parcel_id uuid,
                    p_recipient_name text, p_address text, p_parcel_code text,
                    p_weight numeric, p_length numeric, p_width numeric, p_height numeric)
                RETURNS uuid
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
                AS $create_shipment$
                DECLARE v_commerce_id uuid := nexoruta_app.ctx_uuid('org_id');
                        v_operator_id uuid := nexoruta_app.ctx_uuid('selected_operator_id');
                        v_user_id uuid := nexoruta_app.ctx_uuid('user_id');
                BEGIN
                    IF nexoruta_app.setting('actor_kind') <> 'comercio' OR v_commerce_id IS NULL
                       OR v_operator_id IS NULL OR v_user_id IS NULL OR p_shipment_id IS NULL
                       OR p_recipient_id IS NULL OR p_address_id IS NULL OR p_parcel_id IS NULL
                       OR p_shipment_id <> nexoruta_app.ctx_uuid('shipment_id')
                       OR NOT EXISTS (SELECT 1 FROM public."Comercios" c WHERE c."Id" = v_commerce_id AND c."Estado" = 'Activa')
                       OR NOT EXISTS (SELECT 1 FROM public."AccesosUsuario" a WHERE a."UsuarioId" = v_user_id
                           AND a."ComercioId" = v_commerce_id AND a."Activo" AND a."RolId" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Shipment context is not authorized.' USING ERRCODE = '42501';
                    END IF;

                    -- Lock only the selected operator. This serializes shipment creation with platform
                    -- suspension while keeping commerce from locking or reading inactive operators.
                    PERFORM 1 FROM public."Operadores" o
                     WHERE o."Id" = v_operator_id AND o."Estado" = 'Activa'
                     FOR UPDATE;
                    IF NOT FOUND THEN
                        RAISE EXCEPTION 'Selected operator is unavailable.' USING ERRCODE = 'NXR01';
                    END IF;

                    PERFORM set_config('nexoruta.recipient_id', p_recipient_id::text, true);
                    PERFORM set_config('nexoruta.address_id', p_address_id::text, true);
                    PERFORM set_config('nexoruta.parcel_id', p_parcel_id::text, true);

                    INSERT INTO public."Destinatarios" ("Id", "OperadorId", "Nombre")
                    VALUES (p_recipient_id, v_operator_id, p_recipient_name);
                    INSERT INTO public."Direcciones" ("Id", "OperadorId", "Descripcion")
                    VALUES (p_address_id, v_operator_id, p_address);
                    INSERT INTO public."Envios" ("Id", "OperadorId", "ComercioId", "CreadoPorUsuarioId",
                        "DestinatarioId", "DireccionId", "Estado")
                    VALUES (p_shipment_id, v_operator_id, v_commerce_id, v_user_id,
                        p_recipient_id, p_address_id, 'Admitido');
                    INSERT INTO public."Bultos" ("Id", "OperadorId", "EnvioId", "Codigo", "PesoGramos",
                        "LargoCentimetros", "AnchoCentimetros", "AltoCentimetros")
                    VALUES (p_parcel_id, v_operator_id, p_shipment_id, p_parcel_code, p_weight,
                        p_length, p_width, p_height);
                    RETURN p_shipment_id;
                END
                $create_shipment$;

                CREATE OR REPLACE FUNCTION nexoruta_app.enqueue_password_recovery(
                    p_user_id uuid, p_email_id uuid, p_event_id uuid, p_email_payload jsonb, p_event_payload jsonb)
                RETURNS boolean
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
                AS $recovery_enqueue$
                DECLARE v_now timestamptz := clock_timestamp();
                BEGIN
                    IF p_user_id IS NULL OR p_user_id = '00000000-0000-0000-0000-000000000000'::uuid
                       OR p_email_id IS NULL OR p_event_id IS NULL OR p_email_id = p_event_id
                       OR p_email_payload IS NULL OR jsonb_typeof(p_email_payload) <> 'object'
                       OR p_email_payload <> jsonb_build_object('protectedCommand', p_email_payload -> 'protectedCommand')
                       OR jsonb_typeof(p_email_payload -> 'protectedCommand') IS DISTINCT FROM 'string'
                       OR COALESCE(p_email_payload ->> 'protectedCommand', '') = ''
                       OR p_event_payload IS NULL OR jsonb_typeof(p_event_payload) <> 'object'
                       OR p_event_payload <> jsonb_build_object(
                            'eventId', p_event_payload -> 'eventId',
                            'schemaVersion', p_event_payload -> 'schemaVersion',
                            'usuarioId', p_event_payload -> 'usuarioId',
                            'occurredAt', p_event_payload -> 'occurredAt')
                       OR NOT (p_event_payload ?& ARRAY['eventId','schemaVersion','usuarioId','occurredAt'])
                       OR p_event_payload ->> 'eventId' <> p_event_id::text
                       OR jsonb_typeof(p_event_payload -> 'schemaVersion') <> 'number'
                       OR p_event_payload ->> 'schemaVersion' <> '1'
                       OR p_event_payload ->> 'usuarioId' <> p_user_id::text
                       OR jsonb_typeof(p_event_payload -> 'occurredAt') <> 'string'
                       OR COALESCE(p_event_payload ->> 'occurredAt', '') = '' THEN
                        RAISE EXCEPTION 'Invalid password recovery outbox pair.' USING ERRCODE = '22023';
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM public."Usuarios" u WHERE u."Id" = p_user_id) THEN
                        RETURN false;
                    END IF;
                    PERFORM set_config('nexoruta.queue_mode', 'enqueue_recovery', true);
                    PERFORM set_config('nexoruta.recovery_user_id', p_user_id::text, true);
                    INSERT INTO public."OutboxMessages" (
                        "Id", "OperadorId", "EnvioId", "Ambito", "ClaveAmbito", "Tipo", "VersionContrato",
                        "Destino", "Prioridad", "Estado", "Payload", "EstadoEntrega", "Intentos",
                        "CreadoEnUtc", "ProximoIntentoEnUtc")
                    VALUES
                        (p_email_id, NULL, NULL, 'Identidad', p_user_id, 'PasswordRecoveryEmail', 1,
                         'RabbitMq', 'Alta', 'Pendiente', p_email_payload, 'Pending', 0, v_now, v_now),
                        (p_event_id, NULL, NULL, 'Identidad', p_user_id, 'PasswordRecoveryRequested', 1,
                         'Kafka', 'Baja', 'Pendiente', p_event_payload, NULL, 0, v_now, v_now);
                    RETURN true;
                END
                $recovery_enqueue$;

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

                CREATE OR REPLACE FUNCTION nexoruta_app.mark_outbox_email_delivery(p_message_id uuid, p_state text)
                RETURNS boolean
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
                AS $mark_email_delivery$
                BEGIN
                    IF p_message_id IS NULL OR p_state NOT IN ('Delivered', 'Expired', 'Failed') THEN RETURN false; END IF;
                    PERFORM set_config('nexoruta.queue_mode', 'email_delivery', true);
                    PERFORM set_config('nexoruta.queue_message_id', p_message_id::text, true);
                    UPDATE public."OutboxMessages" m SET "EstadoEntrega" = p_state
                     WHERE m."Id" = p_message_id AND m."Tipo" = 'PasswordRecoveryEmail'
                       AND m."Destino" = 'RabbitMq' AND m."Ambito" = 'Identidad'
                       AND (m."EstadoEntrega" = 'Pending' OR m."EstadoEntrega" = p_state);
                    RETURN FOUND;
                END
                $mark_email_delivery$;

                CREATE OR REPLACE FUNCTION nexoruta_app.get_outbox_email_delivery_state(p_message_id uuid)
                RETURNS text
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
                AS $get_email_delivery$
                DECLARE v_state text;
                BEGIN
                    IF p_message_id IS NULL THEN RETURN NULL; END IF;
                    PERFORM set_config('nexoruta.queue_mode', 'email_delivery_read', true);
                    PERFORM set_config('nexoruta.queue_message_id', p_message_id::text, true);
                    SELECT m."EstadoEntrega" INTO v_state FROM public."OutboxMessages" m
                     WHERE m."Id" = p_message_id AND m."Tipo" = 'PasswordRecoveryEmail'
                       AND m."Destino" = 'RabbitMq' AND m."Ambito" = 'Identidad';
                    RETURN v_state;
                END
                $get_email_delivery$;

                CREATE OR REPLACE FUNCTION nexoruta_app.begin_tracking_job(
                    p_message_id uuid, p_operator_id uuid, p_shipment_id uuid)
                RETURNS boolean
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
                AS $tracking_job$
                DECLARE v_operator_id uuid; v_shipment_id uuid; v_type text; v_destination text;
                        v_scope text; v_contract_version integer; v_state text; v_lease_until timestamptz;
                BEGIN
                    IF p_message_id IS NULL OR p_operator_id IS NULL OR p_shipment_id IS NULL THEN RETURN false; END IF;
                    PERFORM set_config('nexoruta.job_message_id', p_message_id::text, true);
                    SELECT o."OperadorId", o."EnvioId", o."Tipo", o."Destino", o."Ambito", o."VersionContrato", o."Estado", o."ReservaHastaUtc"
                      INTO v_operator_id, v_shipment_id, v_type, v_destination, v_scope, v_contract_version, v_state, v_lease_until
                      FROM public."OutboxMessages" o WHERE o."Id" = p_message_id;
                    IF NOT FOUND OR v_operator_id <> p_operator_id OR v_shipment_id <> p_shipment_id
                       OR v_type <> 'GenerarSeguimiento' OR v_destination <> 'RabbitMq'
                       OR v_scope <> 'Envio' OR v_contract_version <> 1
                       OR (v_state <> 'Publicado' AND NOT (v_state = 'Procesando' AND v_lease_until > clock_timestamp())) THEN
                        RETURN false;
                    END IF;
                    PERFORM set_config('nexoruta.actor_kind', 'worker', true);
                    PERFORM set_config('nexoruta.org_id', v_operator_id::text, true);
                    PERFORM set_config('nexoruta.job_shipment_id', v_shipment_id::text, true);
                    PERFORM set_config('nexoruta.job_consumer', 'GenerarSeguimiento', true);
                    RETURN true;
                END
                $tracking_job$;

                CREATE OR REPLACE FUNCTION nexoruta_app.delete_unused_role(p_role_id uuid)
                RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
                AS $delete_role$
                DECLARE v_org_id uuid; v_actor text := nexoruta_app.setting('actor_kind');
                BEGIN
                    IF v_actor NOT IN ('comercio', 'operador') OR p_role_id IS NULL THEN RETURN false; END IF;
                    PERFORM set_config('nexoruta.role_id', p_role_id::text, true);
                    DELETE FROM public."Roles" r
                     WHERE r."Id" = p_role_id AND NOT r."EsProtegidoSistema"
                       AND ((v_actor = 'comercio' AND r."ComercioId" = nexoruta_app.ctx_uuid('org_id'))
                         OR (v_actor = 'operador' AND r."OperadorId" = nexoruta_app.ctx_uuid('org_id')))
                       AND NOT EXISTS (SELECT 1 FROM public."AccesosUsuario" a WHERE a."RolId" = r."Id")
                       AND NOT EXISTS (SELECT 1 FROM public."RolesPermisos" rp WHERE rp."RolId" = r."Id");
                    RETURN FOUND;
                END
                $delete_role$;

                CREATE OR REPLACE FUNCTION nexoruta_app.lock_owned_role(p_role_id uuid)
                RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
                AS $lock_role$
                DECLARE v_actor text := nexoruta_app.setting('actor_kind');
                BEGIN
                    IF p_role_id IS NULL OR v_actor NOT IN ('comercio', 'operador')
                       OR nexoruta_app.ctx_uuid('org_id') IS NULL THEN RETURN false; END IF;
                    PERFORM set_config('nexoruta.role_id', p_role_id::text, true);
                    PERFORM 1 FROM public."Roles" r
                     WHERE r."Id" = p_role_id
                       AND ((v_actor = 'comercio' AND r."ComercioId" = nexoruta_app.ctx_uuid('org_id'))
                         OR (v_actor = 'operador' AND r."OperadorId" = nexoruta_app.ctx_uuid('org_id')))
                     FOR UPDATE;
                    RETURN FOUND;
                END
                $lock_role$;

                CREATE OR REPLACE FUNCTION nexoruta_app.revoke_org_sessions(p_org_type text, p_org_id uuid)
                RETURNS integer LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
                AS $revoke$
                DECLARE v_count integer;
                BEGIN
                    IF nexoruta_app.setting('actor_kind') <> 'plataforma'
                       OR NOT nexoruta_app.ctx_bool('platform_manage_organizations')
                       OR p_org_type NOT IN ('Comercio', 'Operador') OR p_org_id IS NULL THEN
                        RAISE EXCEPTION 'Platform organization authority is required.';
                    END IF;
                    PERFORM set_config('nexoruta.target_org_id', p_org_id::text, true);
                    PERFORM set_config('nexoruta.target_org_type', p_org_type, true);
                    UPDATE public."AccesosUsuario" a SET "VersionSesion" = a."VersionSesion" + 1
                     WHERE a."TipoOrganizacion" = p_org_type AND a."OrganizacionId" = p_org_id;
                    GET DIAGNOSTICS v_count = ROW_COUNT;
                    RETURN v_count;
                END
                $revoke$;

                CREATE OR REPLACE FUNCTION nexoruta_app.set_organization_state(
                    p_org_type text, p_org_id uuid, p_state text, p_expected_version bigint DEFAULT NULL)
                RETURNS TABLE("EstadoAnterior" text, "EstadoNuevo" text)
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
                AS $set_org_state$
                DECLARE v_previous text; v_version bigint;
                BEGIN
                    IF nexoruta_app.setting('actor_kind') <> 'plataforma'
                       OR NOT nexoruta_app.ctx_bool('platform_manage_organizations')
                       OR p_org_type NOT IN ('Comercio', 'Operador') OR p_org_id IS NULL
                       OR p_state NOT IN ('Activa', 'Suspendida') THEN
                        RAISE EXCEPTION 'Platform organization authority is required.' USING ERRCODE = '42501';
                    END IF;
                    PERFORM set_config('nexoruta.target_org_id', p_org_id::text, true);
                    PERFORM set_config('nexoruta.target_org_type', p_org_type, true);
                    IF p_org_type = 'Comercio' THEN
                        SELECT c."Estado", c."Version" INTO v_previous, v_version FROM public."Comercios" c WHERE c."Id" = p_org_id FOR UPDATE;
                    ELSE
                        SELECT o."Estado", o."Version" INTO v_previous, v_version FROM public."Operadores" o WHERE o."Id" = p_org_id FOR UPDATE;
                    END IF;
                    IF v_previous IS NULL THEN RETURN; END IF;
                    IF v_previous = p_state THEN RETURN QUERY SELECT v_previous, p_state; RETURN; END IF;
                    IF p_expected_version IS NOT NULL AND p_expected_version <> v_version THEN
                        -- Use a non-retryable SQLSTATE: EF's Npgsql execution strategy retries 40001
                        -- before the repository can translate an optimistic concurrency conflict.
                        RAISE EXCEPTION 'Organization version is stale.' USING ERRCODE = 'P0001';
                    END IF;
                    IF p_org_type = 'Comercio' THEN
                        UPDATE public."Comercios" SET "Estado" = p_state, "Version" = "Version" + 1 WHERE "Id" = p_org_id;
                    ELSE
                        UPDATE public."Operadores" SET "Estado" = p_state, "Version" = "Version" + 1 WHERE "Id" = p_org_id;
                    END IF;
                    RETURN QUERY SELECT v_previous, p_state;
                END
                $set_org_state$;

                ALTER FUNCTION nexoruta_app.setting(text) OWNER TO nexoruta_schema_owner;
                ALTER FUNCTION nexoruta_app.ctx_uuid(text) OWNER TO nexoruta_schema_owner;
                ALTER FUNCTION nexoruta_app.ctx_bool(text) OWNER TO nexoruta_schema_owner;
                ALTER FUNCTION nexoruta_app.login_lookup(text) OWNER TO nexoruta_auth_fn;
                ALTER FUNCTION nexoruta_app.lookup_access(uuid) OWNER TO nexoruta_auth_fn;
                ALTER FUNCTION nexoruta_app.lookup_platform_access(uuid) OWNER TO nexoruta_auth_fn;
                ALTER FUNCTION nexoruta_app.lookup_unassigned_identity(uuid) OWNER TO nexoruta_auth_fn;
                ALTER FUNCTION nexoruta_app.public_tracking(text) OWNER TO nexoruta_public_fn;
                ALTER FUNCTION nexoruta_app.create_shipment(uuid, uuid, uuid, uuid, text, text, text, numeric, numeric, numeric, numeric) OWNER TO nexoruta_shipment_fn;
                ALTER FUNCTION nexoruta_app.enqueue_password_recovery(uuid, uuid, uuid, jsonb, jsonb) OWNER TO nexoruta_auth_fn;
                ALTER FUNCTION nexoruta_app.claim_outbox(uuid, text) OWNER TO nexoruta_queue_fn;
                ALTER FUNCTION nexoruta_app.finish_outbox(uuid, uuid, boolean, text) OWNER TO nexoruta_queue_fn;
                ALTER FUNCTION nexoruta_app.mark_outbox_email_delivery(uuid, text) OWNER TO nexoruta_queue_fn;
                ALTER FUNCTION nexoruta_app.get_outbox_email_delivery_state(uuid) OWNER TO nexoruta_queue_fn;
                ALTER FUNCTION nexoruta_app.begin_tracking_job(uuid, uuid, uuid) OWNER TO nexoruta_job_fn;
                ALTER FUNCTION nexoruta_app.delete_unused_role(uuid) OWNER TO nexoruta_role_fn;
                ALTER FUNCTION nexoruta_app.lock_owned_role(uuid) OWNER TO nexoruta_role_fn;
                ALTER FUNCTION nexoruta_app.revoke_org_sessions(text, uuid) OWNER TO nexoruta_org_fn;
                ALTER FUNCTION nexoruta_app.set_organization_state(text, uuid, text, bigint) OWNER TO nexoruta_org_fn;

                REVOKE ALL ON ALL TABLES IN SCHEMA public FROM PUBLIC;
                REVOKE ALL ON ALL TABLES IN SCHEMA public FROM nexoruta_api, nexoruta_worker, nexoruta_bootstrap,
                    nexoruta_auth_fn, nexoruta_public_fn, nexoruta_queue_fn, nexoruta_role_fn, nexoruta_org_fn,
                    nexoruta_shipment_fn, nexoruta_job_fn;
                REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM nexoruta_api, nexoruta_worker, nexoruta_bootstrap,
                    nexoruta_auth_fn, nexoruta_public_fn, nexoruta_queue_fn, nexoruta_role_fn, nexoruta_org_fn,
                    nexoruta_shipment_fn, nexoruta_job_fn;
                REVOKE ALL ON ALL FUNCTIONS IN SCHEMA nexoruta_app FROM PUBLIC;
                REVOKE ALL ON ALL FUNCTIONS IN SCHEMA nexoruta_app FROM nexoruta_api, nexoruta_worker, nexoruta_bootstrap,
                    nexoruta_auth_fn, nexoruta_public_fn, nexoruta_queue_fn, nexoruta_role_fn, nexoruta_org_fn,
                    nexoruta_shipment_fn, nexoruta_job_fn;
                GRANT SELECT, INSERT, UPDATE ON public."Usuarios" TO nexoruta_api, nexoruta_bootstrap;
                GRANT SELECT, INSERT, DELETE ON public."AspNetUserClaims", public."AspNetUserLogins", public."AspNetUserTokens"
                    TO nexoruta_api, nexoruta_bootstrap;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO nexoruta_api, nexoruta_bootstrap;
                GRANT EXECUTE ON FUNCTION nexoruta_app.setting(text), nexoruta_app.ctx_uuid(text), nexoruta_app.ctx_bool(text)
                    TO nexoruta_api, nexoruta_worker, nexoruta_bootstrap, nexoruta_auth_fn, nexoruta_public_fn,
                       nexoruta_queue_fn, nexoruta_role_fn, nexoruta_org_fn, nexoruta_shipment_fn, nexoruta_job_fn;
                GRANT EXECUTE ON FUNCTION nexoruta_app.login_lookup(text), nexoruta_app.lookup_access(uuid),
                    nexoruta_app.lookup_platform_access(uuid), nexoruta_app.lookup_unassigned_identity(uuid)
                    TO nexoruta_api;
                GRANT EXECUTE ON FUNCTION nexoruta_app.public_tracking(text) TO nexoruta_api;
                GRANT EXECUTE ON FUNCTION nexoruta_app.create_shipment(uuid, uuid, uuid, uuid, text, text, text, numeric, numeric, numeric, numeric) TO nexoruta_api;
                GRANT EXECUTE ON FUNCTION nexoruta_app.enqueue_password_recovery(uuid, uuid, uuid, jsonb, jsonb) TO nexoruta_api;
                GRANT EXECUTE ON FUNCTION nexoruta_app.claim_outbox(uuid, text),
                    nexoruta_app.finish_outbox(uuid, uuid, boolean, text),
                    nexoruta_app.mark_outbox_email_delivery(uuid, text),
                    nexoruta_app.get_outbox_email_delivery_state(uuid) TO nexoruta_worker;
                GRANT EXECUTE ON FUNCTION nexoruta_app.begin_tracking_job(uuid, uuid, uuid) TO nexoruta_worker;
                GRANT EXECUTE ON FUNCTION nexoruta_app.delete_unused_role(uuid),
                    nexoruta_app.lock_owned_role(uuid), nexoruta_app.revoke_org_sessions(text, uuid) TO nexoruta_api;
                GRANT EXECUTE ON FUNCTION nexoruta_app.set_organization_state(text, uuid, text, bigint) TO nexoruta_api;

                -- The SECURITY DEFINER owners still obey FORCE RLS; their policies are narrower than
                -- their underlying grants and each function sets a transaction-local, function-derived key.
                CREATE POLICY access_auth_fn_select ON public."AccesosUsuario" FOR SELECT TO nexoruta_auth_fn
                    USING ("Id" = nexoruta_app.ctx_uuid('auth_access_id') OR "UsuarioId" = nexoruta_app.ctx_uuid('auth_user_id'));
                CREATE POLICY permisos_auth_fn_select ON public."Permisos" FOR SELECT TO nexoruta_auth_fn USING (true);

                CREATE POLICY access_role_fn_select_delete ON public."Roles" FOR ALL TO nexoruta_role_fn
                    USING (((nexoruta_app.setting('actor_kind') = 'comercio' AND "ComercioId" = nexoruta_app.ctx_uuid('org_id'))
                         OR (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id')))
                        AND "Id" = nexoruta_app.ctx_uuid('role_id') AND NOT "EsProtegidoSistema")
                    WITH CHECK (false);
                CREATE POLICY role_fn_access_check ON public."AccesosUsuario" FOR SELECT TO nexoruta_role_fn
                    USING ("RolId" = nexoruta_app.ctx_uuid('role_id'));
                CREATE POLICY role_fn_grant_check ON public."RolesPermisos" FOR SELECT TO nexoruta_role_fn
                    USING ("RolId" = nexoruta_app.ctx_uuid('role_id'));
                CREATE POLICY role_fn_delete_grants ON public."RolesPermisos" FOR DELETE TO nexoruta_role_fn
                    USING ("RolId" = nexoruta_app.ctx_uuid('role_id'));
                CREATE POLICY role_fn_lock_owned ON public."Roles" FOR UPDATE TO nexoruta_role_fn
                    USING ("Id" = nexoruta_app.ctx_uuid('role_id') AND
                        ((nexoruta_app.setting('actor_kind') = 'comercio' AND "ComercioId" = nexoruta_app.ctx_uuid('org_id'))
                         OR (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'))))
                    WITH CHECK (false);

                CREATE POLICY queue_fn_outbox_select ON public."OutboxMessages" FOR SELECT TO nexoruta_queue_fn
                    USING (
                        (nexoruta_app.setting('queue_mode') IN ('claim', 'finish', 'email_delivery', 'email_delivery_read')
                            AND "Id" = nexoruta_app.ctx_uuid('queue_message_id'))
                        OR (nexoruta_app.setting('queue_mode') = 'claim' AND
                            "Destino" = nexoruta_app.setting('queue_destination') AND
                            (nexoruta_app.setting('queue_destination') = 'Kafka' OR
                             ("Estado" IN ('Pendiente', 'Error') AND "ProximoIntentoEnUtc" <= now()) OR
                             ("Estado" = 'Procesando' AND "ReservaHastaUtc" <= now())))
                        OR (nexoruta_app.setting('queue_mode') = 'finish' AND
                            "Id" = nexoruta_app.ctx_uuid('queue_message_id') AND
                            "ProcesadorId" = nexoruta_app.ctx_uuid('queue_processor_id') AND
                            "ReservaHastaUtc" > now())
                        OR (nexoruta_app.setting('queue_mode') = 'email_delivery' AND
                            "Id" = nexoruta_app.ctx_uuid('queue_message_id') AND "Tipo" = 'PasswordRecoveryEmail')
                        OR (nexoruta_app.setting('queue_mode') = 'email_delivery_read' AND
                            "Id" = nexoruta_app.ctx_uuid('queue_message_id') AND "Tipo" = 'PasswordRecoveryEmail')
                    );
                CREATE POLICY queue_fn_outbox_update ON public."OutboxMessages" FOR UPDATE TO nexoruta_queue_fn
                    USING (
                        (nexoruta_app.setting('queue_mode') = 'claim' AND
                            "Destino" = nexoruta_app.setting('queue_destination') AND
                            ((("Estado" IN ('Pendiente', 'Error') AND "ProximoIntentoEnUtc" <= now()) OR
                              ("Estado" = 'Procesando' AND "ReservaHastaUtc" <= now())) OR
                              "Id" = nexoruta_app.ctx_uuid('queue_message_id')))
                        OR (nexoruta_app.setting('queue_mode') = 'finish' AND
                            "Id" = nexoruta_app.ctx_uuid('queue_message_id') AND
                            "ProcesadorId" = nexoruta_app.ctx_uuid('queue_processor_id') AND
                            "ReservaHastaUtc" > now())
                        OR (nexoruta_app.setting('queue_mode') = 'email_delivery' AND
                            "Id" = nexoruta_app.ctx_uuid('queue_message_id') AND "Tipo" = 'PasswordRecoveryEmail')
                    )
                    -- The SECURITY DEFINER functions constrain each UPDATE's target in USING/WHERE;
                    -- table CHECK constraints enforce the scope and registered type/destination tuple.
                    WITH CHECK (true);
                GRANT SELECT, UPDATE ON public."OutboxMessages" TO nexoruta_queue_fn;

                CREATE POLICY org_fn_select_access ON public."AccesosUsuario" FOR SELECT TO nexoruta_org_fn
                    USING ("OrganizacionId" = nexoruta_app.ctx_uuid('target_org_id')
                       AND "TipoOrganizacion" = nexoruta_app.setting('target_org_type'));
                CREATE POLICY org_fn_update_access ON public."AccesosUsuario" FOR UPDATE TO nexoruta_org_fn
                    USING ("OrganizacionId" = nexoruta_app.ctx_uuid('target_org_id')
                       AND "TipoOrganizacion" = nexoruta_app.setting('target_org_type'))
                    WITH CHECK ("OrganizacionId" = nexoruta_app.ctx_uuid('target_org_id')
                       AND "TipoOrganizacion" = nexoruta_app.setting('target_org_type'));
                GRANT SELECT ("TipoOrganizacion", "OrganizacionId", "VersionSesion"), UPDATE ("VersionSesion")
                    ON public."AccesosUsuario" TO nexoruta_org_fn;
                CREATE POLICY org_fn_commerce_select ON public."Comercios" FOR SELECT TO nexoruta_org_fn
                    USING ("Id" = nexoruta_app.ctx_uuid('target_org_id') AND nexoruta_app.setting('target_org_type') = 'Comercio');
                CREATE POLICY org_fn_commerce_update ON public."Comercios" FOR UPDATE TO nexoruta_org_fn
                    USING ("Id" = nexoruta_app.ctx_uuid('target_org_id') AND nexoruta_app.setting('target_org_type') = 'Comercio')
                    WITH CHECK ("Id" = nexoruta_app.ctx_uuid('target_org_id') AND nexoruta_app.setting('target_org_type') = 'Comercio');
                CREATE POLICY org_fn_operator_select ON public."Operadores" FOR SELECT TO nexoruta_org_fn
                    USING ("Id" = nexoruta_app.ctx_uuid('target_org_id') AND nexoruta_app.setting('target_org_type') = 'Operador');
                CREATE POLICY org_fn_operator_update ON public."Operadores" FOR UPDATE TO nexoruta_org_fn
                    USING ("Id" = nexoruta_app.ctx_uuid('target_org_id') AND nexoruta_app.setting('target_org_type') = 'Operador')
                    WITH CHECK ("Id" = nexoruta_app.ctx_uuid('target_org_id') AND nexoruta_app.setting('target_org_type') = 'Operador');
                GRANT SELECT ON public."Comercios", public."Operadores" TO nexoruta_org_fn;
                GRANT UPDATE ("Estado", "Version") ON public."Comercios", public."Operadores" TO nexoruta_org_fn;

                -- Table grants and actor-bound RLS policies.
                GRANT SELECT, INSERT, UPDATE ON public."Operadores", public."Comercios" TO nexoruta_api;
                REVOKE UPDATE ON public."Operadores", public."Comercios" FROM nexoruta_api;
                GRANT UPDATE ("Nombre", "Version") ON public."Operadores", public."Comercios" TO nexoruta_api;
                CREATE POLICY operators_api_select ON public."Operadores" FOR SELECT TO nexoruta_api
                    USING ((nexoruta_app.setting('actor_kind') = 'operador' AND "Id" = nexoruta_app.ctx_uuid('org_id'))
                        OR (nexoruta_app.setting('actor_kind') = 'comercio' AND ("Estado" = 'Activa' OR EXISTS (
                            SELECT 1 FROM public."Envios" e WHERE e."OperadorId" = "Operadores"."Id"
                              AND e."ComercioId" = nexoruta_app.ctx_uuid('org_id'))))
                        OR (nexoruta_app.setting('actor_kind') = 'plataforma' AND nexoruta_app.ctx_bool('platform_manage_organizations'))
                        OR (nexoruta_app.setting('actor_kind') = 'suspendida' AND "Id" = nexoruta_app.ctx_uuid('org_id'))
                        OR (nexoruta_app.setting('actor_kind') = 'bootstrap' AND "Id" = nexoruta_app.ctx_uuid('bootstrap_operator_id')));
                CREATE POLICY operators_api_insert ON public."Operadores" FOR INSERT TO nexoruta_api
                    WITH CHECK ((nexoruta_app.setting('actor_kind') = 'identidad' AND "Id" = nexoruta_app.ctx_uuid('selected_org_id'))
                        OR (nexoruta_app.setting('actor_kind') = 'plataforma' AND nexoruta_app.ctx_bool('platform_manage_organizations') AND "Id" = nexoruta_app.ctx_uuid('selected_org_id')));
                CREATE POLICY operators_api_update ON public."Operadores" FOR UPDATE TO nexoruta_api
                    USING ((nexoruta_app.setting('actor_kind') = 'operador' AND "Id" = nexoruta_app.ctx_uuid('org_id'))
                        OR (nexoruta_app.setting('actor_kind') = 'plataforma' AND nexoruta_app.ctx_bool('platform_manage_organizations')))
                    WITH CHECK ((nexoruta_app.setting('actor_kind') = 'operador' AND "Id" = nexoruta_app.ctx_uuid('org_id'))
                        OR (nexoruta_app.setting('actor_kind') = 'plataforma' AND nexoruta_app.ctx_bool('platform_manage_organizations')));
                CREATE POLICY operators_bootstrap ON public."Operadores" FOR ALL TO nexoruta_bootstrap
                    USING ("Id" = nexoruta_app.ctx_uuid('bootstrap_operator_id'))
                    WITH CHECK ("Id" = nexoruta_app.ctx_uuid('bootstrap_operator_id'));
                CREATE POLICY operators_auth_fn_select ON public."Operadores" FOR SELECT TO nexoruta_auth_fn USING ("Id" = nexoruta_app.ctx_uuid('auth_org_id'));

                CREATE POLICY commerces_api_select ON public."Comercios" FOR SELECT TO nexoruta_api
                    USING ((nexoruta_app.setting('actor_kind') = 'comercio' AND "Id" = nexoruta_app.ctx_uuid('org_id'))
                        OR (nexoruta_app.setting('actor_kind') = 'operador' AND EXISTS (
                            SELECT 1 FROM public."Envios" e WHERE e."ComercioId" = "Comercios"."Id"
                              AND e."OperadorId" = nexoruta_app.ctx_uuid('org_id')))
                        OR (nexoruta_app.setting('actor_kind') = 'plataforma' AND nexoruta_app.ctx_bool('platform_manage_organizations'))
                        OR (nexoruta_app.setting('actor_kind') = 'suspendida' AND "Id" = nexoruta_app.ctx_uuid('org_id'))
                        OR (nexoruta_app.setting('actor_kind') = 'bootstrap' AND "Id" = nexoruta_app.ctx_uuid('bootstrap_commerce_id')));
                CREATE POLICY commerces_api_insert ON public."Comercios" FOR INSERT TO nexoruta_api
                    WITH CHECK ((nexoruta_app.setting('actor_kind') = 'identidad' AND "Id" = nexoruta_app.ctx_uuid('selected_org_id'))
                        OR (nexoruta_app.setting('actor_kind') = 'plataforma' AND nexoruta_app.ctx_bool('platform_manage_organizations') AND "Id" = nexoruta_app.ctx_uuid('selected_org_id')));
                CREATE POLICY commerces_api_update ON public."Comercios" FOR UPDATE TO nexoruta_api
                    USING ((nexoruta_app.setting('actor_kind') = 'comercio' AND "Id" = nexoruta_app.ctx_uuid('org_id'))
                        OR (nexoruta_app.setting('actor_kind') = 'plataforma' AND nexoruta_app.ctx_bool('platform_manage_organizations')))
                    WITH CHECK ((nexoruta_app.setting('actor_kind') = 'comercio' AND "Id" = nexoruta_app.ctx_uuid('org_id'))
                        OR (nexoruta_app.setting('actor_kind') = 'plataforma' AND nexoruta_app.ctx_bool('platform_manage_organizations')));
                CREATE POLICY commerces_bootstrap ON public."Comercios" FOR ALL TO nexoruta_bootstrap
                    USING ("Id" = nexoruta_app.ctx_uuid('bootstrap_commerce_id'))
                    WITH CHECK ("Id" = nexoruta_app.ctx_uuid('bootstrap_commerce_id'));
                CREATE POLICY commerces_auth_fn_select ON public."Comercios" FOR SELECT TO nexoruta_auth_fn USING ("Id" = nexoruta_app.ctx_uuid('auth_org_id'));

                GRANT SELECT, INSERT, UPDATE ON public."AccesosUsuario" TO nexoruta_api;
                CREATE POLICY user_access_api_select ON public."AccesosUsuario" FOR SELECT TO nexoruta_api
                    USING (((nexoruta_app.setting('actor_kind') IN ('comercio','operador') AND
                              "TipoOrganizacion" = CASE WHEN nexoruta_app.setting('actor_kind') = 'comercio' THEN 'Comercio' ELSE 'Operador' END AND
                              "OrganizacionId" = nexoruta_app.ctx_uuid('org_id')))
                        OR (nexoruta_app.setting('actor_kind') = 'identidad' AND "UsuarioId" = nexoruta_app.ctx_uuid('user_id'))
                        OR (nexoruta_app.setting('actor_kind') = 'suspendida' AND "UsuarioId" = nexoruta_app.ctx_uuid('user_id')
                            AND "OrganizacionId" = nexoruta_app.ctx_uuid('org_id'))
                        OR (nexoruta_app.setting('actor_kind') = 'bootstrap' AND "UsuarioId" = nexoruta_app.ctx_uuid('user_id')
                            AND (("OperadorId" IS NOT NULL AND "OperadorId" = nexoruta_app.ctx_uuid('bootstrap_operator_id'))
                              OR ("ComercioId" IS NOT NULL AND "ComercioId" = nexoruta_app.ctx_uuid('bootstrap_commerce_id')))));
                CREATE POLICY user_access_api_insert ON public."AccesosUsuario" FOR INSERT TO nexoruta_api
                    WITH CHECK (((nexoruta_app.setting('actor_kind') IN ('comercio','operador') AND
                              "TipoOrganizacion" = CASE WHEN nexoruta_app.setting('actor_kind') = 'comercio' THEN 'Comercio' ELSE 'Operador' END AND
                              "OrganizacionId" = nexoruta_app.ctx_uuid('org_id') AND NOT "EsPropietario")
                        OR (nexoruta_app.setting('actor_kind') IN ('identidad','plataforma') AND
                            "UsuarioId" = nexoruta_app.ctx_uuid('target_user_id') AND "RolId" = nexoruta_app.ctx_uuid('target_role_id')
                            AND "OrganizacionId" = nexoruta_app.ctx_uuid('selected_org_id') AND "EsPropietario"))
                        OR (nexoruta_app.setting('actor_kind') = 'bootstrap' AND "UsuarioId" = nexoruta_app.ctx_uuid('user_id')
                            AND "EsPropietario" AND (("OperadorId" IS NOT NULL AND "OperadorId" = nexoruta_app.ctx_uuid('bootstrap_operator_id'))
                              OR ("ComercioId" IS NOT NULL AND "ComercioId" = nexoruta_app.ctx_uuid('bootstrap_commerce_id')))));
                CREATE POLICY user_access_api_update ON public."AccesosUsuario" FOR UPDATE TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind') IN ('comercio','operador') AND
                           "TipoOrganizacion" = CASE WHEN nexoruta_app.setting('actor_kind') = 'comercio' THEN 'Comercio' ELSE 'Operador' END AND
                           "OrganizacionId" = nexoruta_app.ctx_uuid('org_id') AND NOT "EsPropietario")
                    WITH CHECK (nexoruta_app.setting('actor_kind') IN ('comercio','operador') AND
                           "TipoOrganizacion" = CASE WHEN nexoruta_app.setting('actor_kind') = 'comercio' THEN 'Comercio' ELSE 'Operador' END AND
                           "OrganizacionId" = nexoruta_app.ctx_uuid('org_id') AND NOT "EsPropietario");
                CREATE POLICY user_access_bootstrap ON public."AccesosUsuario" FOR ALL TO nexoruta_bootstrap
                    USING ("UsuarioId" = nexoruta_app.ctx_uuid('user_id') AND "EsPropietario" AND
                        (("OperadorId" IS NOT NULL AND "OperadorId" = nexoruta_app.ctx_uuid('bootstrap_operator_id'))
                         OR ("ComercioId" IS NOT NULL AND "ComercioId" = nexoruta_app.ctx_uuid('bootstrap_commerce_id'))))
                    WITH CHECK ("UsuarioId" = nexoruta_app.ctx_uuid('user_id') AND "EsPropietario" AND
                        (("OperadorId" IS NOT NULL AND "OperadorId" = nexoruta_app.ctx_uuid('bootstrap_operator_id'))
                         OR ("ComercioId" IS NOT NULL AND "ComercioId" = nexoruta_app.ctx_uuid('bootstrap_commerce_id'))));
                CREATE POLICY user_access_auth_fn_select ON public."AccesosUsuario" FOR SELECT TO nexoruta_auth_fn
                    USING ("Id" = nexoruta_app.ctx_uuid('auth_access_id') OR "UsuarioId" = nexoruta_app.ctx_uuid('auth_user_id'));

                GRANT SELECT ON public."AccesosPlataforma" TO nexoruta_api;
                CREATE POLICY platform_access_api_select ON public."AccesosPlataforma" FOR SELECT TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind') = 'plataforma' AND
                           "UsuarioId" = nexoruta_app.ctx_uuid('user_id') AND "AccesoId" = nexoruta_app.ctx_uuid('access_id'));
                CREATE POLICY platform_access_auth_fn_select ON public."AccesosPlataforma" FOR SELECT TO nexoruta_auth_fn
                    USING ("AccesoId" = nexoruta_app.ctx_uuid('auth_access_id') OR "UsuarioId" = nexoruta_app.ctx_uuid('auth_user_id'));

                GRANT SELECT, INSERT, UPDATE ON public."Roles", public."RolesPlataforma", public."RolesPlataformaPermisos" TO nexoruta_api;
                REVOKE UPDATE ON public."Roles", public."RolesPlataforma", public."RolesPlataformaPermisos" FROM nexoruta_api;
                GRANT UPDATE ("Nombre") ON public."Roles" TO nexoruta_api;
                GRANT SELECT, INSERT, DELETE ON public."RolesPermisos" TO nexoruta_api;
                CREATE POLICY roles_api_select ON public."Roles" FOR SELECT TO nexoruta_api
                    USING ((nexoruta_app.setting('actor_kind') IN ('comercio','operador') AND "OrganizacionId" = nexoruta_app.ctx_uuid('org_id')
                           AND "TipoOrganizacion" = CASE WHEN nexoruta_app.setting('actor_kind') = 'comercio' THEN 'Comercio' ELSE 'Operador' END)
                        OR (nexoruta_app.setting('actor_kind') = 'identidad' AND "OrganizacionId" = nexoruta_app.ctx_uuid('selected_org_id'))
                        OR (nexoruta_app.setting('actor_kind') = 'plataforma' AND "OrganizacionId" = nexoruta_app.ctx_uuid('selected_org_id'))
                        OR (nexoruta_app.setting('actor_kind') = 'bootstrap' AND (("OperadorId" IS NOT NULL AND "OperadorId" = nexoruta_app.ctx_uuid('bootstrap_operator_id')) OR ("ComercioId" IS NOT NULL AND "ComercioId" = nexoruta_app.ctx_uuid('bootstrap_commerce_id')))));
                CREATE POLICY roles_api_insert ON public."Roles" FOR INSERT TO nexoruta_api
                    WITH CHECK (((nexoruta_app.setting('actor_kind') IN ('comercio','operador') AND "OrganizacionId" = nexoruta_app.ctx_uuid('org_id'))
                        OR (nexoruta_app.setting('actor_kind') IN ('identidad','plataforma') AND "OrganizacionId" = nexoruta_app.ctx_uuid('selected_org_id'))));
                CREATE POLICY roles_api_update ON public."Roles" FOR UPDATE TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind') IN ('comercio','operador') AND "OrganizacionId" = nexoruta_app.ctx_uuid('org_id') AND NOT "EsProtegidoSistema")
                    WITH CHECK (nexoruta_app.setting('actor_kind') IN ('comercio','operador') AND "OrganizacionId" = nexoruta_app.ctx_uuid('org_id') AND NOT "EsProtegidoSistema");
                CREATE POLICY roles_bootstrap ON public."Roles" FOR ALL TO nexoruta_bootstrap
                    USING (("OperadorId" IS NOT NULL AND "OperadorId" = nexoruta_app.ctx_uuid('bootstrap_operator_id')) OR ("ComercioId" IS NOT NULL AND "ComercioId" = nexoruta_app.ctx_uuid('bootstrap_commerce_id')))
                    WITH CHECK (("OperadorId" IS NOT NULL AND "OperadorId" = nexoruta_app.ctx_uuid('bootstrap_operator_id')) OR ("ComercioId" IS NOT NULL AND "ComercioId" = nexoruta_app.ctx_uuid('bootstrap_commerce_id')));
                CREATE POLICY roles_auth_fn_select ON public."Roles" FOR SELECT TO nexoruta_auth_fn USING ("Id" = nexoruta_app.ctx_uuid('auth_role_id'));
                CREATE POLICY roles_role_fn_delete ON public."Roles" FOR DELETE TO nexoruta_role_fn
                    USING ("Id" = nexoruta_app.ctx_uuid('role_id') AND NOT "EsProtegidoSistema"
                        AND ((nexoruta_app.setting('actor_kind') = 'comercio' AND "ComercioId" = nexoruta_app.ctx_uuid('org_id'))
                          OR (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'))));
                CREATE POLICY roles_role_fn_select ON public."Roles" FOR SELECT TO nexoruta_role_fn
                    USING ("Id" = nexoruta_app.ctx_uuid('role_id'));

                GRANT SELECT, INSERT, DELETE ON public."RolesPermisos" TO nexoruta_api;
                CREATE POLICY role_permissions_api_select ON public."RolesPermisos" FOR SELECT TO nexoruta_api
                    USING (EXISTS (SELECT 1 FROM public."Roles" r WHERE r."Id" = "RolesPermisos"."RolId"));
                CREATE POLICY role_permissions_api_insert ON public."RolesPermisos" FOR INSERT TO nexoruta_api
                    WITH CHECK (EXISTS (SELECT 1 FROM public."Roles" r WHERE r."Id" = "RolesPermisos"."RolId"));
                CREATE POLICY role_permissions_api_delete ON public."RolesPermisos" FOR DELETE TO nexoruta_api
                    USING (EXISTS (SELECT 1 FROM public."Roles" r WHERE r."Id" = "RolesPermisos"."RolId"));
                CREATE POLICY role_permissions_bootstrap ON public."RolesPermisos" FOR ALL TO nexoruta_bootstrap
                    USING (EXISTS (SELECT 1 FROM public."Roles" r WHERE r."Id" = "RolesPermisos"."RolId" AND
                        ((r."OperadorId" IS NOT NULL AND r."OperadorId" = nexoruta_app.ctx_uuid('bootstrap_operator_id')) OR
                         (r."ComercioId" IS NOT NULL AND r."ComercioId" = nexoruta_app.ctx_uuid('bootstrap_commerce_id')))))
                    WITH CHECK (EXISTS (SELECT 1 FROM public."Roles" r WHERE r."Id" = "RolesPermisos"."RolId" AND
                        ((r."OperadorId" IS NOT NULL AND r."OperadorId" = nexoruta_app.ctx_uuid('bootstrap_operator_id')) OR
                         (r."ComercioId" IS NOT NULL AND r."ComercioId" = nexoruta_app.ctx_uuid('bootstrap_commerce_id')))));

                CREATE POLICY platform_roles_api_select ON public."RolesPlataforma" FOR SELECT TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind') = 'plataforma' AND "Id" = (SELECT a."RolId" FROM public."AccesosPlataforma" a WHERE a."AccesoId" = nexoruta_app.ctx_uuid('access_id')));
                CREATE POLICY platform_roles_api_update ON public."RolesPlataforma" FOR UPDATE TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind') = 'plataforma' AND nexoruta_app.ctx_bool('platform_manage_organizations') AND "Id" = (SELECT a."RolId" FROM public."AccesosPlataforma" a WHERE a."AccesoId" = nexoruta_app.ctx_uuid('access_id')))
                    WITH CHECK (nexoruta_app.setting('actor_kind') = 'plataforma' AND nexoruta_app.ctx_bool('platform_manage_organizations'));
                CREATE POLICY platform_roles_bootstrap ON public."RolesPlataforma" FOR ALL TO nexoruta_bootstrap
                    USING ("EsProtegidoSistema") WITH CHECK ("EsProtegidoSistema");
                CREATE POLICY platform_roles_auth_fn_select ON public."RolesPlataforma" FOR SELECT TO nexoruta_auth_fn
                    USING ("Id" = nexoruta_app.ctx_uuid('auth_role_id'));

                CREATE POLICY platform_grants_api_select ON public."RolesPlataformaPermisos" FOR SELECT TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind') = 'plataforma' AND "RolId" = (SELECT a."RolId" FROM public."AccesosPlataforma" a WHERE a."AccesoId" = nexoruta_app.ctx_uuid('access_id')));
                CREATE POLICY platform_grants_api_modify ON public."RolesPlataformaPermisos" FOR ALL TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind') = 'plataforma' AND nexoruta_app.ctx_bool('platform_manage_organizations') AND "RolId" = (SELECT a."RolId" FROM public."AccesosPlataforma" a WHERE a."AccesoId" = nexoruta_app.ctx_uuid('access_id')))
                    WITH CHECK (nexoruta_app.setting('actor_kind') = 'plataforma' AND nexoruta_app.ctx_bool('platform_manage_organizations') AND "RolId" = (SELECT a."RolId" FROM public."AccesosPlataforma" a WHERE a."AccesoId" = nexoruta_app.ctx_uuid('access_id')));
                CREATE POLICY platform_grants_bootstrap ON public."RolesPlataformaPermisos" FOR ALL TO nexoruta_bootstrap
                    USING ("RolId" IN (SELECT r."Id" FROM public."RolesPlataforma" r WHERE r."EsProtegidoSistema"))
                    WITH CHECK ("RolId" IN (SELECT r."Id" FROM public."RolesPlataforma" r WHERE r."EsProtegidoSistema"));
                CREATE POLICY platform_grants_auth_fn_select ON public."RolesPlataformaPermisos" FOR SELECT TO nexoruta_auth_fn
                    USING ("RolId" = nexoruta_app.ctx_uuid('auth_role_id'));

                GRANT SELECT, INSERT ON public."Permisos" TO nexoruta_api;
                GRANT SELECT, INSERT ON public."Permisos" TO nexoruta_bootstrap;
                CREATE POLICY permissions_catalog_select ON public."Permisos" FOR SELECT TO nexoruta_api, nexoruta_bootstrap
                    USING (nexoruta_app.setting('actor_kind') IN ('comercio','operador','plataforma','identidad','bootstrap'));
                CREATE POLICY permissions_catalog_insert ON public."Permisos" FOR INSERT TO nexoruta_api, nexoruta_bootstrap
                    WITH CHECK (nexoruta_app.setting('actor_kind') IN ('plataforma','identidad','bootstrap') AND
                        "Codigo" IN ('usuarios.gestionar','configuracion.gestionar','envios.crear','envios.consultar','envios.gestionar','plataforma.organizaciones.gestionar'));
                CREATE POLICY permissions_auth_fn_select ON public."Permisos" FOR SELECT TO nexoruta_auth_fn USING (true);

                GRANT SELECT, INSERT, UPDATE ON public."SolicitudesBajaOrganizacion", public."SolicitudesReactivacionOrganizacion" TO nexoruta_api;
                REVOKE UPDATE ON public."SolicitudesBajaOrganizacion", public."SolicitudesReactivacionOrganizacion" FROM nexoruta_api;
                GRANT UPDATE ("Estado", "ResueltaPorUsuarioId", "ResueltaEnUtc", "MotivoResolucion", "Version")
                    ON public."SolicitudesBajaOrganizacion", public."SolicitudesReactivacionOrganizacion" TO nexoruta_api;
                CREATE POLICY baja_api_select ON public."SolicitudesBajaOrganizacion" FOR SELECT TO nexoruta_api
                    USING ((nexoruta_app.setting('actor_kind') IN ('comercio','operador','suspendida') AND "TipoOrganizacion" = CASE WHEN nexoruta_app.setting('actor_kind') = 'comercio' THEN 'Comercio' WHEN nexoruta_app.setting('actor_kind') = 'operador' THEN 'Operador' ELSE nexoruta_app.setting('target_org_type') END AND "OrganizacionId" = nexoruta_app.ctx_uuid('org_id'))
                        OR (nexoruta_app.setting('actor_kind') = 'plataforma' AND nexoruta_app.ctx_bool('platform_manage_organizations')));
                CREATE POLICY baja_api_insert ON public."SolicitudesBajaOrganizacion" FOR INSERT TO nexoruta_api
                    WITH CHECK (nexoruta_app.setting('actor_kind') IN ('comercio','operador') AND "OrganizacionId" = nexoruta_app.ctx_uuid('org_id') AND "TipoOrganizacion" = CASE WHEN nexoruta_app.setting('actor_kind') = 'comercio' THEN 'Comercio' ELSE 'Operador' END);
                CREATE POLICY baja_api_update ON public."SolicitudesBajaOrganizacion" FOR UPDATE TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind') = 'plataforma' AND nexoruta_app.ctx_bool('platform_manage_organizations'))
                    WITH CHECK (nexoruta_app.setting('actor_kind') = 'plataforma' AND nexoruta_app.ctx_bool('platform_manage_organizations'));

                CREATE POLICY reactivacion_api_select ON public."SolicitudesReactivacionOrganizacion" FOR SELECT TO nexoruta_api
                    USING ((nexoruta_app.setting('actor_kind') IN ('comercio','operador') AND "OrganizacionId" = nexoruta_app.ctx_uuid('org_id') AND "TipoOrganizacion" = CASE WHEN nexoruta_app.setting('actor_kind') = 'comercio' THEN 'Comercio' ELSE 'Operador' END)
                        OR (nexoruta_app.setting('actor_kind') = 'suspendida' AND "OrganizacionId" = nexoruta_app.ctx_uuid('org_id') AND "TipoOrganizacion" = nexoruta_app.setting('target_org_type'))
                        OR (nexoruta_app.setting('actor_kind') = 'plataforma' AND nexoruta_app.ctx_bool('platform_manage_organizations')));
                CREATE POLICY reactivacion_api_insert ON public."SolicitudesReactivacionOrganizacion" FOR INSERT TO nexoruta_api
                    WITH CHECK (nexoruta_app.setting('actor_kind') IN ('comercio','operador','suspendida') AND "OrganizacionId" = nexoruta_app.ctx_uuid('org_id')
                        AND "TipoOrganizacion" = CASE WHEN nexoruta_app.setting('actor_kind') = 'comercio' THEN 'Comercio' WHEN nexoruta_app.setting('actor_kind') = 'operador' THEN 'Operador' ELSE nexoruta_app.setting('target_org_type') END);
                CREATE POLICY reactivacion_api_update ON public."SolicitudesReactivacionOrganizacion" FOR UPDATE TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind') = 'plataforma' AND nexoruta_app.ctx_bool('platform_manage_organizations'))
                    WITH CHECK (nexoruta_app.setting('actor_kind') = 'plataforma' AND nexoruta_app.ctx_bool('platform_manage_organizations'));

                GRANT SELECT, INSERT ON public."CambiosEstadoOrganizacion" TO nexoruta_api;
                CREATE POLICY org_history_api_select ON public."CambiosEstadoOrganizacion" FOR SELECT TO nexoruta_api
                    USING ((nexoruta_app.setting('actor_kind') IN ('comercio','operador','suspendida') AND "OrganizacionId" = nexoruta_app.ctx_uuid('org_id')
                        AND "TipoOrganizacion" = CASE WHEN nexoruta_app.setting('actor_kind') = 'comercio' THEN 'Comercio' WHEN nexoruta_app.setting('actor_kind') = 'operador' THEN 'Operador' ELSE nexoruta_app.setting('target_org_type') END)
                        OR (nexoruta_app.setting('actor_kind') = 'plataforma' AND nexoruta_app.ctx_bool('platform_manage_organizations')));
                CREATE POLICY org_history_api_insert ON public."CambiosEstadoOrganizacion" FOR INSERT TO nexoruta_api
                    WITH CHECK (nexoruta_app.setting('actor_kind') = 'plataforma' AND nexoruta_app.ctx_bool('platform_manage_organizations'));

                GRANT SELECT, INSERT, UPDATE ON public."Envios" TO nexoruta_api;
                CREATE POLICY shipment_api_select ON public."Envios" FOR SELECT TO nexoruta_api
                    USING ((nexoruta_app.setting('actor_kind') = 'comercio' AND "ComercioId" = nexoruta_app.ctx_uuid('org_id'))
                        OR (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id')));
                CREATE POLICY shipment_api_insert ON public."Envios" FOR INSERT TO nexoruta_api
                    WITH CHECK (nexoruta_app.setting('actor_kind') = 'comercio'
                        AND "ComercioId" = nexoruta_app.ctx_uuid('org_id')
                        AND "CreadoPorUsuarioId" = nexoruta_app.ctx_uuid('user_id')
                        AND "OperadorId" = nexoruta_app.ctx_uuid('selected_operator_id')
                        AND "Id" = nexoruta_app.ctx_uuid('shipment_id')
                        AND EXISTS (SELECT 1 FROM public."Comercios" c WHERE c."Id" = "Envios"."ComercioId" AND c."Estado" = 'Activa')
                        AND EXISTS (SELECT 1 FROM public."Operadores" o WHERE o."Id" = "Envios"."OperadorId" AND o."Estado" = 'Activa'));
                CREATE POLICY shipment_api_update ON public."Envios" FOR UPDATE TO nexoruta_api
                    USING ((nexoruta_app.setting('actor_kind') = 'comercio' AND "ComercioId" = nexoruta_app.ctx_uuid('org_id'))
                        OR (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id')))
                    WITH CHECK ((nexoruta_app.setting('actor_kind') = 'comercio' AND "ComercioId" = nexoruta_app.ctx_uuid('org_id'))
                        OR (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id')));
                CREATE POLICY shipment_auth_fn_read ON public."Envios" FOR SELECT TO nexoruta_auth_fn USING (false);
                CREATE POLICY shipment_public_read ON public."Envios" FOR SELECT TO nexoruta_public_fn
                    USING (EXISTS (SELECT 1 FROM public."EnlacesSeguimiento" l WHERE l."EnvioId" = "Envios"."Id"
                        AND l."OperadorId" = "Envios"."OperadorId" AND l."TokenHash" = nexoruta_app.setting('public_token_hash')
                        AND l."RevocadoEnUtc" IS NULL AND l."ExpiraEnUtc" > now()));
                CREATE POLICY shipment_worker_read ON public."Envios" FOR SELECT TO nexoruta_worker
                    USING ("Id" = nexoruta_app.ctx_uuid('job_shipment_id') AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));

                GRANT SELECT, INSERT, UPDATE ON public."Destinatarios", public."Direcciones" TO nexoruta_api;
                CREATE POLICY recipient_api_select ON public."Destinatarios" FOR SELECT TO nexoruta_api
                    USING (EXISTS (SELECT 1 FROM public."Envios" e WHERE e."OperadorId" = "Destinatarios"."OperadorId"
                        AND e."DestinatarioId" = "Destinatarios"."Id"));
                CREATE POLICY recipient_api_insert ON public."Destinatarios" FOR INSERT TO nexoruta_api
                    WITH CHECK (nexoruta_app.setting('actor_kind') = 'comercio'
                        AND "OperadorId" = nexoruta_app.ctx_uuid('selected_operator_id')
                        AND nexoruta_app.ctx_uuid('shipment_id') IS NOT NULL
                        AND EXISTS (SELECT 1 FROM public."Operadores" o WHERE o."Id" = "OperadorId" AND o."Estado" = 'Activa'));
                CREATE POLICY recipient_api_update ON public."Destinatarios" FOR UPDATE TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind') = 'comercio' AND EXISTS (SELECT 1 FROM public."Envios" e
                        WHERE e."OperadorId" = "Destinatarios"."OperadorId" AND e."DestinatarioId" = "Destinatarios"."Id"
                          AND e."ComercioId" = nexoruta_app.ctx_uuid('org_id')))
                    WITH CHECK (nexoruta_app.setting('actor_kind') = 'comercio' AND EXISTS (SELECT 1 FROM public."Envios" e
                        WHERE e."OperadorId" = "Destinatarios"."OperadorId" AND e."DestinatarioId" = "Destinatarios"."Id"
                          AND e."ComercioId" = nexoruta_app.ctx_uuid('org_id')));
                CREATE POLICY address_api_select ON public."Direcciones" FOR SELECT TO nexoruta_api
                    USING (EXISTS (SELECT 1 FROM public."Envios" e WHERE e."OperadorId" = "Direcciones"."OperadorId"
                        AND e."DireccionId" = "Direcciones"."Id"));
                CREATE POLICY address_api_insert ON public."Direcciones" FOR INSERT TO nexoruta_api
                    WITH CHECK (nexoruta_app.setting('actor_kind') = 'comercio'
                        AND "OperadorId" = nexoruta_app.ctx_uuid('selected_operator_id')
                        AND nexoruta_app.ctx_uuid('shipment_id') IS NOT NULL
                        AND EXISTS (SELECT 1 FROM public."Operadores" o WHERE o."Id" = "OperadorId" AND o."Estado" = 'Activa'));
                CREATE POLICY address_api_update ON public."Direcciones" FOR UPDATE TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind') = 'comercio' AND EXISTS (SELECT 1 FROM public."Envios" e
                        WHERE e."OperadorId" = "Direcciones"."OperadorId" AND e."DireccionId" = "Direcciones"."Id"
                          AND e."ComercioId" = nexoruta_app.ctx_uuid('org_id')))
                    WITH CHECK (nexoruta_app.setting('actor_kind') = 'comercio' AND EXISTS (SELECT 1 FROM public."Envios" e
                        WHERE e."OperadorId" = "Direcciones"."OperadorId" AND e."DireccionId" = "Direcciones"."Id"
                          AND e."ComercioId" = nexoruta_app.ctx_uuid('org_id')));

                GRANT SELECT, INSERT, UPDATE ON public."Bultos" TO nexoruta_api;
                CREATE POLICY parcels_api_select ON public."Bultos" FOR SELECT TO nexoruta_api
                    USING (EXISTS (SELECT 1 FROM public."Envios" e WHERE e."OperadorId" = "Bultos"."OperadorId" AND e."Id" = "Bultos"."EnvioId"));
                CREATE POLICY parcels_api_insert ON public."Bultos" FOR INSERT TO nexoruta_api
                    WITH CHECK (nexoruta_app.setting('actor_kind') = 'comercio'
                        AND "OperadorId" = nexoruta_app.ctx_uuid('selected_operator_id')
                        AND "EnvioId" = nexoruta_app.ctx_uuid('shipment_id')
                        AND EXISTS (SELECT 1 FROM public."Envios" e WHERE e."Id" = "Bultos"."EnvioId" AND e."OperadorId" = "Bultos"."OperadorId" AND e."ComercioId" = nexoruta_app.ctx_uuid('org_id')));
                CREATE POLICY parcels_api_update ON public."Bultos" FOR UPDATE TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind') = 'comercio' AND EXISTS (SELECT 1 FROM public."Envios" e WHERE e."Id" = "Bultos"."EnvioId" AND e."OperadorId" = "Bultos"."OperadorId" AND e."ComercioId" = nexoruta_app.ctx_uuid('org_id')))
                    WITH CHECK (nexoruta_app.setting('actor_kind') = 'comercio' AND EXISTS (SELECT 1 FROM public."Envios" e WHERE e."Id" = "Bultos"."EnvioId" AND e."OperadorId" = "Bultos"."OperadorId" AND e."ComercioId" = nexoruta_app.ctx_uuid('org_id')));

                GRANT SELECT, INSERT, UPDATE ON public."EnlacesSeguimiento" TO nexoruta_api, nexoruta_worker;
                REVOKE INSERT, UPDATE ON public."EnlacesSeguimiento" FROM nexoruta_api;
                REVOKE UPDATE ON public."EnlacesSeguimiento" FROM nexoruta_worker;
                CREATE POLICY tracking_links_api_select ON public."EnlacesSeguimiento" FOR SELECT TO nexoruta_api
                    USING (EXISTS (SELECT 1 FROM public."Envios" e WHERE e."OperadorId" = "EnlacesSeguimiento"."OperadorId" AND e."Id" = "EnlacesSeguimiento"."EnvioId"));
                CREATE POLICY tracking_links_api_insert ON public."EnlacesSeguimiento" FOR INSERT TO nexoruta_api
                    WITH CHECK (nexoruta_app.setting('actor_kind') IN ('comercio','operador')
                        AND "OperadorId" = COALESCE(nexoruta_app.ctx_uuid('selected_operator_id'), nexoruta_app.ctx_uuid('org_id'))
                        AND EXISTS (SELECT 1 FROM public."Envios" e WHERE e."Id" = "EnlacesSeguimiento"."EnvioId" AND e."OperadorId" = "EnlacesSeguimiento"."OperadorId"));
                CREATE POLICY tracking_links_api_update ON public."EnlacesSeguimiento" FOR UPDATE TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind') IN ('comercio','operador') AND EXISTS (SELECT 1 FROM public."Envios" e WHERE e."Id" = "EnlacesSeguimiento"."EnvioId" AND e."OperadorId" = "EnlacesSeguimiento"."OperadorId"))
                    WITH CHECK (nexoruta_app.setting('actor_kind') IN ('comercio','operador') AND EXISTS (SELECT 1 FROM public."Envios" e WHERE e."Id" = "EnlacesSeguimiento"."EnvioId" AND e."OperadorId" = "EnlacesSeguimiento"."OperadorId"));
                CREATE POLICY tracking_links_worker_select ON public."EnlacesSeguimiento" FOR SELECT TO nexoruta_worker
                    USING ("EnvioId" = nexoruta_app.ctx_uuid('job_shipment_id') AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY tracking_links_worker_insert ON public."EnlacesSeguimiento" FOR INSERT TO nexoruta_worker
                    WITH CHECK ("EnvioId" = nexoruta_app.ctx_uuid('job_shipment_id') AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY tracking_links_public ON public."EnlacesSeguimiento" FOR SELECT TO nexoruta_public_fn
                    USING ("TokenHash" = nexoruta_app.setting('public_token_hash') AND "RevocadoEnUtc" IS NULL AND "ExpiraEnUtc" > now());

                GRANT SELECT ON public."EntregasSeguimiento" TO nexoruta_api;
                GRANT SELECT, INSERT ON public."EntregasSeguimiento" TO nexoruta_worker;
                CREATE POLICY deliveries_api_select ON public."EntregasSeguimiento" FOR SELECT TO nexoruta_api
                    USING (EXISTS (SELECT 1 FROM public."Envios" e WHERE e."Id" = "EntregasSeguimiento"."EnvioId" AND e."OperadorId" = "EntregasSeguimiento"."OperadorId"));
                CREATE POLICY deliveries_worker_select ON public."EntregasSeguimiento" FOR SELECT TO nexoruta_worker
                    USING ("EnvioId" = nexoruta_app.ctx_uuid('job_shipment_id') AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY deliveries_worker_insert ON public."EntregasSeguimiento" FOR INSERT TO nexoruta_worker
                    WITH CHECK ("EnvioId" = nexoruta_app.ctx_uuid('job_shipment_id') AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY deliveries_public ON public."EntregasSeguimiento" FOR SELECT TO nexoruta_public_fn
                    USING ("TokenHash" = nexoruta_app.setting('public_token_hash'));

                GRANT SELECT, INSERT ON public."ProcessedMessages" TO nexoruta_worker;
                CREATE POLICY processed_worker_select ON public."ProcessedMessages" FOR SELECT TO nexoruta_worker
                    USING ("MessageId" = nexoruta_app.ctx_uuid('job_message_id') AND "Consumer" = nexoruta_app.setting('job_consumer')
                        AND EXISTS (SELECT 1 FROM public."OutboxMessages" o WHERE o."Id" = "MessageId"
                            AND o."OperadorId" = nexoruta_app.ctx_uuid('org_id') AND o."EnvioId" = nexoruta_app.ctx_uuid('job_shipment_id')
                            AND o."Ambito" = 'Envio' AND o."VersionContrato" = 1
                            AND o."Tipo" = 'GenerarSeguimiento' AND o."Destino" = 'RabbitMq'));
                CREATE POLICY processed_worker_insert ON public."ProcessedMessages" FOR INSERT TO nexoruta_worker
                    WITH CHECK ("MessageId" = nexoruta_app.ctx_uuid('job_message_id') AND "Consumer" = nexoruta_app.setting('job_consumer')
                        AND EXISTS (SELECT 1 FROM public."OutboxMessages" o WHERE o."Id" = "MessageId"
                            AND o."OperadorId" = nexoruta_app.ctx_uuid('org_id') AND o."EnvioId" = nexoruta_app.ctx_uuid('job_shipment_id')
                            AND o."Ambito" = 'Envio' AND o."VersionContrato" = 1
                            AND o."Tipo" = 'GenerarSeguimiento' AND o."Destino" = 'RabbitMq'));
                CREATE POLICY worker_outbox_message_select ON public."OutboxMessages" FOR SELECT TO nexoruta_worker
                    USING ("Id" = nexoruta_app.ctx_uuid('job_message_id') AND "OperadorId" = nexoruta_app.ctx_uuid('org_id')
                        AND "EnvioId" = nexoruta_app.ctx_uuid('job_shipment_id') AND "Ambito" = 'Envio' AND "VersionContrato" = 1
                        AND "Tipo" = 'GenerarSeguimiento' AND "Destino" = 'RabbitMq');
                CREATE POLICY job_fn_outbox_select ON public."OutboxMessages" FOR SELECT TO nexoruta_job_fn
                    USING ("Id" = nexoruta_app.ctx_uuid('job_message_id') AND "Ambito" = 'Envio'
                        AND "VersionContrato" = 1 AND "Tipo" = 'GenerarSeguimiento' AND "Destino" = 'RabbitMq');
                GRANT SELECT ON public."OutboxMessages" TO nexoruta_worker, nexoruta_job_fn;

                GRANT INSERT ON public."OutboxMessages" TO nexoruta_api;
                CREATE POLICY outbox_api_insert ON public."OutboxMessages" FOR INSERT TO nexoruta_api
                    WITH CHECK (nexoruta_app.setting('actor_kind') = 'comercio'
                        AND "Ambito" = 'Envio' AND "ClaveAmbito" = "EnvioId"
                        AND "OperadorId" = nexoruta_app.ctx_uuid('selected_operator_id')
                        AND "EnvioId" = nexoruta_app.ctx_uuid('shipment_id')
                        AND "Estado" = 'Pendiente' AND "Intentos" = 0
                        AND "ProcesadorId" IS NULL AND "ReservaHastaUtc" IS NULL
                        AND "PublicadoEnUtc" IS NULL AND "UltimoError" IS NULL AND "EstadoEntrega" IS NULL
                        AND (("Tipo" = 'GenerarSeguimiento' AND "VersionContrato" = 1 AND "Destino" = 'RabbitMq' AND "Prioridad" = 'Alta')
                          OR ("Tipo" = 'EnvioCreado' AND "VersionContrato" = 1 AND "Destino" = 'Kafka' AND "Prioridad" = 'Normal'))
                        AND EXISTS (SELECT 1 FROM public."Envios" e WHERE e."Id" = "OutboxMessages"."EnvioId"
                            AND e."OperadorId" = "OutboxMessages"."OperadorId"
                            AND e."ComercioId" = nexoruta_app.ctx_uuid('org_id')));
                GRANT INSERT ON public."OutboxMessages" TO nexoruta_auth_fn;
                CREATE POLICY auth_fn_recovery_outbox_insert ON public."OutboxMessages" FOR INSERT TO nexoruta_auth_fn
                    WITH CHECK (nexoruta_app.setting('queue_mode') = 'enqueue_recovery'
                        AND "Ambito" = 'Identidad' AND "ClaveAmbito" = nexoruta_app.ctx_uuid('recovery_user_id')
                        AND "OperadorId" IS NULL AND "EnvioId" IS NULL
                        AND "Estado" = 'Pendiente' AND "Intentos" = 0
                        AND "ProcesadorId" IS NULL AND "ReservaHastaUtc" IS NULL
                        AND "PublicadoEnUtc" IS NULL AND "UltimoError" IS NULL
                        AND (("Tipo" = 'PasswordRecoveryEmail' AND "VersionContrato" = 1 AND "Destino" = 'RabbitMq' AND "Prioridad" = 'Alta' AND "EstadoEntrega" = 'Pending')
                          OR ("Tipo" = 'PasswordRecoveryRequested' AND "VersionContrato" = 1 AND "Destino" = 'Kafka' AND "Prioridad" = 'Baja' AND "EstadoEntrega" IS NULL)));

                CREATE POLICY public_fn_link_lookup ON public."EnlacesSeguimiento" FOR SELECT TO nexoruta_public_fn
                    USING ("TokenHash" = nexoruta_app.setting('public_token_hash') AND "RevocadoEnUtc" IS NULL AND "ExpiraEnUtc" > now());
                CREATE POLICY public_fn_delivery_lookup ON public."EntregasSeguimiento" FOR SELECT TO nexoruta_public_fn
                    USING ("TokenHash" = nexoruta_app.setting('public_token_hash'));
                CREATE POLICY public_fn_envio_lookup ON public."Envios" FOR SELECT TO nexoruta_public_fn
                    USING (EXISTS (SELECT 1 FROM public."EnlacesSeguimiento" l WHERE l."EnvioId" = "Envios"."Id"
                        AND l."OperadorId" = "Envios"."OperadorId" AND l."TokenHash" = nexoruta_app.setting('public_token_hash')
                        AND l."RevocadoEnUtc" IS NULL AND l."ExpiraEnUtc" > now()));

                CREATE POLICY shipment_fn_commerce_select ON public."Comercios" FOR SELECT TO nexoruta_shipment_fn
                    USING ("Id" = nexoruta_app.ctx_uuid('org_id') AND nexoruta_app.setting('actor_kind') = 'comercio');
                CREATE POLICY shipment_fn_operator_select ON public."Operadores" FOR SELECT TO nexoruta_shipment_fn
                    USING ("Id" = nexoruta_app.ctx_uuid('selected_operator_id'));
                CREATE POLICY shipment_fn_operator_lock ON public."Operadores" FOR UPDATE TO nexoruta_shipment_fn
                    USING (nexoruta_app.setting('actor_kind') = 'comercio'
                        AND "Id" = nexoruta_app.ctx_uuid('selected_operator_id') AND "Estado" = 'Activa')
                    WITH CHECK (false);
                CREATE POLICY shipment_fn_access_select ON public."AccesosUsuario" FOR SELECT TO nexoruta_shipment_fn
                    USING ("UsuarioId" = nexoruta_app.ctx_uuid('user_id') AND "ComercioId" = nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY shipment_fn_recipient_insert ON public."Destinatarios" FOR INSERT TO nexoruta_shipment_fn
                    WITH CHECK ("Id" = nexoruta_app.ctx_uuid('recipient_id') AND "OperadorId" = nexoruta_app.ctx_uuid('selected_operator_id'));
                CREATE POLICY shipment_fn_address_insert ON public."Direcciones" FOR INSERT TO nexoruta_shipment_fn
                    WITH CHECK ("Id" = nexoruta_app.ctx_uuid('address_id') AND "OperadorId" = nexoruta_app.ctx_uuid('selected_operator_id'));
                CREATE POLICY shipment_fn_shipment_insert ON public."Envios" FOR INSERT TO nexoruta_shipment_fn
                    WITH CHECK ("Id" = nexoruta_app.ctx_uuid('shipment_id') AND "ComercioId" = nexoruta_app.ctx_uuid('org_id')
                        AND "OperadorId" = nexoruta_app.ctx_uuid('selected_operator_id') AND "CreadoPorUsuarioId" = nexoruta_app.ctx_uuid('user_id'));
                CREATE POLICY shipment_fn_parcel_insert ON public."Bultos" FOR INSERT TO nexoruta_shipment_fn
                    WITH CHECK ("Id" = nexoruta_app.ctx_uuid('parcel_id') AND "EnvioId" = nexoruta_app.ctx_uuid('shipment_id')
                        AND "OperadorId" = nexoruta_app.ctx_uuid('selected_operator_id'));
                GRANT SELECT ON public."Comercios", public."Operadores", public."AccesosUsuario" TO nexoruta_shipment_fn;
                GRANT UPDATE ("Estado") ON public."Operadores" TO nexoruta_shipment_fn;
                GRANT INSERT ON public."Destinatarios", public."Direcciones", public."Envios", public."Bultos" TO nexoruta_shipment_fn;

                -- All function owners receive only their table operations. API/worker do not inherit them.
                GRANT SELECT ON public."AccesosUsuario", public."AccesosPlataforma", public."Roles", public."RolesPlataforma",
                    public."RolesPlataformaPermisos", public."Permisos", public."Operadores", public."Comercios", public."Usuarios" TO nexoruta_auth_fn;
                GRANT SELECT ON public."Envios", public."EnlacesSeguimiento", public."EntregasSeguimiento" TO nexoruta_public_fn;
                GRANT SELECT, UPDATE ON public."OutboxMessages" TO nexoruta_queue_fn;
                GRANT SELECT, DELETE ON public."Roles" TO nexoruta_role_fn;
                GRANT SELECT ON public."AccesosUsuario", public."RolesPermisos" TO nexoruta_role_fn;
                GRANT UPDATE ("Nombre") ON public."Roles" TO nexoruta_role_fn;
                GRANT SELECT ("TipoOrganizacion", "OrganizacionId", "VersionSesion"), UPDATE ("VersionSesion")
                    ON public."AccesosUsuario" TO nexoruta_org_fn;
                REVOKE ALL ON public."Envios", public."Destinatarios", public."Direcciones", public."Bultos",
                    public."EnlacesSeguimiento", public."EntregasSeguimiento", public."OutboxMessages", public."ProcessedMessages"
                    FROM nexoruta_api;
                GRANT SELECT ON public."Envios", public."Destinatarios", public."Direcciones", public."Bultos",
                    public."EnlacesSeguimiento" TO nexoruta_api;
                GRANT INSERT ON public."OutboxMessages" TO nexoruta_api;
                REVOKE UPDATE ON public."AccesosUsuario" FROM nexoruta_api;
                GRANT UPDATE ("Activo", "RolId", "VersionSesion") ON public."AccesosUsuario" TO nexoruta_api;
                REVOKE INSERT, UPDATE ON public."Destinatarios", public."Direcciones", public."Envios", public."Bultos" FROM nexoruta_api;
                REVOKE UPDATE ON public."OutboxMessages" FROM nexoruta_api;
                REVOKE INSERT, UPDATE ON public."EnlacesSeguimiento" FROM nexoruta_api;
                REVOKE UPDATE ON public."RolesPermisos" FROM nexoruta_api;
                GRANT SELECT ON public."EntregasSeguimiento" TO nexoruta_api;
                GRANT SELECT ON public."Envios" TO nexoruta_worker;
                GRANT SELECT, INSERT ON public."EnlacesSeguimiento", public."EntregasSeguimiento" TO nexoruta_worker;

                -- Bootstrap is an explicit, one-shot process; it can touch only the configured initial pair.
                GRANT SELECT, INSERT, UPDATE ON public."Operadores", public."Comercios", public."Roles",
                    public."RolesPlataforma" TO nexoruta_bootstrap;
                GRANT SELECT, INSERT ON public."AccesosUsuario", public."Permisos", public."AccesosPlataforma",
                    public."RolesPlataformaPermisos" TO nexoruta_bootstrap;
                GRANT SELECT, INSERT, DELETE ON public."RolesPermisos" TO nexoruta_bootstrap;
                CREATE POLICY bootstrap_platform_role_select ON public."RolesPlataforma" FOR SELECT TO nexoruta_bootstrap
                    USING ("EsProtegidoSistema");
                CREATE POLICY bootstrap_platform_grants_select ON public."RolesPlataformaPermisos" FOR SELECT TO nexoruta_bootstrap
                    USING ("RolId" IN (SELECT "RolId" FROM public."AccesosPlataforma" WHERE "Id" = 1));
                CREATE POLICY bootstrap_platform_grants_insert ON public."RolesPlataformaPermisos" FOR INSERT TO nexoruta_bootstrap
                    WITH CHECK ("RolId" IN (SELECT "RolId" FROM public."AccesosPlataforma" WHERE "Id" = 1));
                CREATE POLICY bootstrap_platform_access_select ON public."AccesosPlataforma" FOR SELECT TO nexoruta_bootstrap USING ("Id" = 1);
                CREATE POLICY bootstrap_platform_access_insert ON public."AccesosPlataforma" FOR INSERT TO nexoruta_bootstrap
                    WITH CHECK ("Id" = 1);

                -- No application grant can physically delete shipments, users, or access rows.
                REVOKE DELETE ON public."AccesosUsuario", public."AccesosPlataforma", public."Operadores", public."Comercios",
                    public."SolicitudesBajaOrganizacion", public."SolicitudesReactivacionOrganizacion", public."CambiosEstadoOrganizacion",
                    public."Destinatarios", public."Direcciones", public."Envios", public."Bultos", public."EnlacesSeguimiento",
                    public."EntregasSeguimiento", public."Roles", public."Permisos" FROM nexoruta_api;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $drop_policies$
                DECLARE policy_row record;
                BEGIN
                    FOR policy_row IN
                        SELECT schemaname, tablename, policyname
                        FROM pg_policies
                        WHERE schemaname = 'public' AND tablename = ANY(ARRAY[
                            'AccesosPlataforma','RolesPlataforma','RolesPlataformaPermisos','Operadores','Comercios',
                            'AccesosUsuario','SolicitudesBajaOrganizacion','SolicitudesReactivacionOrganizacion',
                            'CambiosEstadoOrganizacion','Destinatarios','Direcciones','Envios','Bultos',
                            'EnlacesSeguimiento','EntregasSeguimiento','OutboxMessages','ProcessedMessages',
                            'Roles','Permisos','RolesPermisos'])
                    LOOP
                        EXECUTE format('DROP POLICY %I ON %I.%I', policy_row.policyname,
                            policy_row.schemaname, policy_row.tablename);
                    END LOOP;
                END
                $drop_policies$;
                REVOKE ALL ON ALL TABLES IN SCHEMA public FROM nexoruta_api, nexoruta_worker, nexoruta_bootstrap,
                    nexoruta_auth_fn, nexoruta_public_fn, nexoruta_queue_fn, nexoruta_role_fn, nexoruta_org_fn,
                    nexoruta_shipment_fn, nexoruta_job_fn, PUBLIC;
                REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM nexoruta_api, nexoruta_worker, nexoruta_bootstrap,
                    nexoruta_auth_fn, nexoruta_public_fn, nexoruta_queue_fn, nexoruta_role_fn, nexoruta_org_fn,
                    nexoruta_shipment_fn, nexoruta_job_fn;
                ALTER TABLE public."AccesosPlataforma" DISABLE ROW LEVEL SECURITY;
                ALTER TABLE public."RolesPlataforma" DISABLE ROW LEVEL SECURITY;
                ALTER TABLE public."RolesPlataformaPermisos" DISABLE ROW LEVEL SECURITY;
                ALTER TABLE public."Operadores" DISABLE ROW LEVEL SECURITY;
                ALTER TABLE public."Comercios" DISABLE ROW LEVEL SECURITY;
                ALTER TABLE public."AccesosUsuario" DISABLE ROW LEVEL SECURITY;
                ALTER TABLE public."SolicitudesBajaOrganizacion" DISABLE ROW LEVEL SECURITY;
                ALTER TABLE public."SolicitudesReactivacionOrganizacion" DISABLE ROW LEVEL SECURITY;
                ALTER TABLE public."CambiosEstadoOrganizacion" DISABLE ROW LEVEL SECURITY;
                ALTER TABLE public."Destinatarios" DISABLE ROW LEVEL SECURITY;
                ALTER TABLE public."Direcciones" DISABLE ROW LEVEL SECURITY;
                ALTER TABLE public."Envios" DISABLE ROW LEVEL SECURITY;
                ALTER TABLE public."Bultos" DISABLE ROW LEVEL SECURITY;
                ALTER TABLE public."EnlacesSeguimiento" DISABLE ROW LEVEL SECURITY;
                ALTER TABLE public."EntregasSeguimiento" DISABLE ROW LEVEL SECURITY;
                ALTER TABLE public."OutboxMessages" DISABLE ROW LEVEL SECURITY;
                ALTER TABLE public."ProcessedMessages" DISABLE ROW LEVEL SECURITY;
                ALTER TABLE public."Roles" DISABLE ROW LEVEL SECURITY;
                ALTER TABLE public."Permisos" DISABLE ROW LEVEL SECURITY;
                ALTER TABLE public."RolesPermisos" DISABLE ROW LEVEL SECURITY;
                ALTER TABLE public."AccesosPlataforma" OWNER TO CURRENT_USER;
                ALTER TABLE public."RolesPlataforma" OWNER TO CURRENT_USER;
                ALTER TABLE public."RolesPlataformaPermisos" OWNER TO CURRENT_USER;
                ALTER TABLE public."Operadores" OWNER TO CURRENT_USER;
                ALTER TABLE public."Comercios" OWNER TO CURRENT_USER;
                ALTER TABLE public."Usuarios" OWNER TO CURRENT_USER;
                ALTER TABLE public."AspNetUserClaims" OWNER TO CURRENT_USER;
                ALTER TABLE public."AspNetUserLogins" OWNER TO CURRENT_USER;
                ALTER TABLE public."AspNetUserTokens" OWNER TO CURRENT_USER;
                ALTER TABLE public."AccesosUsuario" OWNER TO CURRENT_USER;
                ALTER TABLE public."SolicitudesBajaOrganizacion" OWNER TO CURRENT_USER;
                ALTER TABLE public."SolicitudesReactivacionOrganizacion" OWNER TO CURRENT_USER;
                ALTER TABLE public."CambiosEstadoOrganizacion" OWNER TO CURRENT_USER;
                ALTER TABLE public."Destinatarios" OWNER TO CURRENT_USER;
                ALTER TABLE public."Direcciones" OWNER TO CURRENT_USER;
                ALTER TABLE public."Envios" OWNER TO CURRENT_USER;
                ALTER TABLE public."Bultos" OWNER TO CURRENT_USER;
                ALTER TABLE public."EnlacesSeguimiento" OWNER TO CURRENT_USER;
                ALTER TABLE public."EntregasSeguimiento" OWNER TO CURRENT_USER;
                ALTER TABLE public."OutboxMessages" OWNER TO CURRENT_USER;
                ALTER TABLE public."ProcessedMessages" OWNER TO CURRENT_USER;
                ALTER TABLE public."Roles" OWNER TO CURRENT_USER;
                ALTER TABLE public."Permisos" OWNER TO CURRENT_USER;
                ALTER TABLE public."RolesPermisos" OWNER TO CURRENT_USER;
                DROP SCHEMA IF EXISTS nexoruta_app CASCADE;
                """);
        }
    }
}
