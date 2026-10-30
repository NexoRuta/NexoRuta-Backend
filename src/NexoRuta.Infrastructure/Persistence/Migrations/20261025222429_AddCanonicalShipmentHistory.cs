using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCanonicalShipmentHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EnvioEventos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    ComercioId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvioId = table.Column<Guid>(type: "uuid", nullable: false),
                    Tipo = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    EstadoAnterior = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    EstadoNuevo = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    EstadoObservado = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    VersionAnterior = table.Column<long>(type: "bigint", nullable: true),
                    VersionNueva = table.Column<long>(type: "bigint", nullable: false),
                    VersionReglasId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperacionJornadaId = table.Column<Guid>(type: "uuid", nullable: true),
                    CorrelacionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorTipo = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ActorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorAccesoId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorOrganizacionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorProceso = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    AutorizacionPublicaId = table.Column<Guid>(type: "uuid", nullable: true),
                    OrigenTipo = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    OrigenId = table.Column<Guid>(type: "uuid", nullable: true),
                    CodigoHecho = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Procedencia = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OcurridaEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RegistradaEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()"),
                    CancelacionEnvioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnvioEventos", x => x.Id);
                    table.CheckConstraint("CK_EnvioEventos_Actor", "(\"ActorTipo\" = 'Usuario' AND \"ActorUsuarioId\" IS NOT NULL AND \"ActorProceso\" IS NULL AND \"AutorizacionPublicaId\" IS NULL) OR (\"ActorTipo\" = 'Publico' AND \"ActorUsuarioId\" IS NULL AND \"ActorAccesoId\" IS NULL AND \"ActorProceso\" IS NULL AND \"AutorizacionPublicaId\" IS NOT NULL) OR (\"ActorTipo\" IN ('Proceso','Migracion') AND \"ActorUsuarioId\" IS NULL AND \"ActorAccesoId\" IS NULL AND \"ActorProceso\" IS NOT NULL AND \"AutorizacionPublicaId\" IS NULL)");
                    table.CheckConstraint("CK_EnvioEventos_Forma", "(\"Tipo\" = 'Creacion' AND \"EstadoAnterior\" IS NULL AND \"EstadoNuevo\" = 'Admitido' AND \"EstadoObservado\" IS NULL AND \"VersionAnterior\" IS NULL AND \"VersionNueva\" = 0 AND \"CodigoHecho\" IS NULL) OR (\"Tipo\" = 'TransicionEstado' AND \"EstadoAnterior\" IS NOT NULL AND \"EstadoNuevo\" IS NOT NULL AND \"EstadoAnterior\" <> \"EstadoNuevo\" AND \"EstadoObservado\" IS NULL AND \"VersionAnterior\" >= 0 AND \"VersionNueva\" > \"VersionAnterior\" AND \"CodigoHecho\" IS NULL) OR (\"Tipo\" = 'EstadoObservadoInicio' AND \"EstadoAnterior\" IS NULL AND \"EstadoNuevo\" IS NULL AND \"EstadoObservado\" IS NOT NULL AND \"VersionAnterior\" IS NULL AND \"CodigoHecho\" IS NULL) OR (\"Tipo\" = 'Hecho' AND \"EstadoAnterior\" IS NULL AND \"EstadoNuevo\" IS NULL AND \"EstadoObservado\" IS NOT NULL AND \"VersionAnterior\" >= 0 AND \"VersionNueva\" > \"VersionAnterior\" AND \"CodigoHecho\" IS NOT NULL)");
                    table.CheckConstraint("CK_EnvioEventos_OrigenCancelacion", "\"CancelacionEnvioId\" IS NULL OR (\"Tipo\" = 'TransicionEstado' AND \"EstadoNuevo\" = 'Cancelado' AND \"OrigenTipo\" = 'CancelacionEnvio' AND \"OrigenId\" = \"CancelacionEnvioId\")");
                    table.CheckConstraint("CK_EnvioEventos_Procedencia", "\"Procedencia\" IN ('EnLinea','Corte','Backfill')");
                    table.CheckConstraint("CK_EnvioEventos_Tipo", "\"Tipo\" IN ('Creacion','TransicionEstado','EstadoObservadoInicio','Hecho')");
                    table.ForeignKey(
                        name: "FK_EnvioEventos_CancelacionesEnvio_CancelacionEnvioId",
                        column: x => x.CancelacionEnvioId,
                        principalTable: "CancelacionesEnvio",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EnvioEventos_Envios_ComercioId_EnvioId",
                        columns: x => new { x.ComercioId, x.EnvioId },
                        principalTable: "Envios",
                        principalColumns: new[] { "ComercioId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EnvioEventos_Envios_OperadorId_EnvioId",
                        columns: x => new { x.OperadorId, x.EnvioId },
                        principalTable: "Envios",
                        principalColumns: new[] { "OperadorId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EnvioEventos_VersionesReglasOperativas_OperadorId_VersionRe~",
                        columns: x => new { x.OperadorId, x.VersionReglasId },
                        principalTable: "VersionesReglasOperativas",
                        principalColumns: new[] { "OperadorId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EnvioEventos_CancelacionEnvioId",
                table: "EnvioEventos",
                column: "CancelacionEnvioId",
                unique: true,
                filter: "\"CancelacionEnvioId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EnvioEventos_ComercioId_EnvioId_RegistradaEnUtc",
                table: "EnvioEventos",
                columns: new[] { "ComercioId", "EnvioId", "RegistradaEnUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_EnvioEventos_OperadorId_EnvioId",
                table: "EnvioEventos",
                columns: new[] { "OperadorId", "EnvioId" },
                unique: true,
                filter: "\"Tipo\" = 'EstadoObservadoInicio'");

            migrationBuilder.CreateIndex(
                name: "IX_EnvioEventos_OperadorId_EnvioId_OrigenTipo_OrigenId_CodigoH~",
                table: "EnvioEventos",
                columns: new[] { "OperadorId", "EnvioId", "OrigenTipo", "OrigenId", "CodigoHecho" },
                unique: true,
                filter: "\"Tipo\" = 'Hecho' AND \"OrigenId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EnvioEventos_OperadorId_EnvioId_RegistradaEnUtc",
                table: "EnvioEventos",
                columns: new[] { "OperadorId", "EnvioId", "RegistradaEnUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_EnvioEventos_OperadorId_EnvioId_VersionAnterior_VersionNueva",
                table: "EnvioEventos",
                columns: new[] { "OperadorId", "EnvioId", "VersionAnterior", "VersionNueva" },
                unique: true,
                filter: "\"Tipo\" = 'TransicionEstado'");

            migrationBuilder.CreateIndex(
                name: "IX_EnvioEventos_OperadorId_VersionReglasId",
                table: "EnvioEventos",
                columns: new[] { "OperadorId", "VersionReglasId" });

            migrationBuilder.Sql("""
                DO $roles$
                DECLARE v_history_role oid;
                BEGIN
                    SELECT oid INTO v_history_role FROM pg_roles WHERE rolname = 'nexoruta_history_fn';
                    IF v_history_role IS NULL THEN
                        BEGIN
                            CREATE ROLE nexoruta_history_fn NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
                        EXCEPTION WHEN duplicate_object THEN
                            NULL;
                        END;
                        SELECT oid INTO v_history_role FROM pg_roles WHERE rolname = 'nexoruta_history_fn';
                    ELSIF EXISTS (SELECT 1 FROM pg_roles WHERE oid = v_history_role
                        AND (rolcanlogin OR rolinherit OR rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls))
                        OR EXISTS (SELECT 1 FROM pg_auth_members WHERE roleid = v_history_role) THEN
                        RAISE EXCEPTION 'Role nexoruta_history_fn already exists with unsafe attributes or memberships.' USING ERRCODE = '42501';
                    END IF;
                END $roles$;

                ALTER TABLE public."EnvioEventos" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."EnvioEventos" ALTER CONSTRAINT "FK_EnvioEventos_CancelacionesEnvio_CancelacionEnvioId"
                    DEFERRABLE INITIALLY DEFERRED;
                GRANT USAGE ON SCHEMA public, nexoruta_app TO nexoruta_history_fn;
                GRANT EXECUTE ON FUNCTION nexoruta_app.setting(text), nexoruta_app.ctx_uuid(text) TO nexoruta_history_fn;
                GRANT SELECT ON public."Envios" TO nexoruta_history_fn;
                GRANT INSERT, SELECT ON public."EnvioEventos" TO nexoruta_history_fn;
                GRANT SELECT ON public."EnvioEventos" TO nexoruta_api;
                REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON public."EnvioEventos" FROM PUBLIC, nexoruta_api, nexoruta_worker;
                REVOKE UPDATE, DELETE, TRUNCATE ON public."EnvioEventos" FROM nexoruta_history_fn;

                INSERT INTO public."EnvioEventos" (
                    "OperadorId", "ComercioId", "EnvioId", "Tipo", "EstadoObservado", "VersionNueva",
                    "VersionReglasId", "ActorTipo", "ActorProceso", "OrigenTipo", "Procedencia", "OcurridaEnUtc")
                SELECT e."OperadorId", e."ComercioId", e."Id", 'EstadoObservadoInicio', e."Estado", e."Version",
                    e."VersionReglasId", 'Migracion', 'corte-historia-inmutable', 'CorteMigracion', 'Corte', NULL
                FROM public."Envios" e
                ON CONFLICT ("OperadorId", "EnvioId") WHERE "Tipo" = 'EstadoObservadoInicio' DO NOTHING;

                INSERT INTO public."EnvioEventos" (
                    "OperadorId", "ComercioId", "EnvioId", "Tipo", "EstadoAnterior", "EstadoNuevo",
                    "VersionAnterior", "VersionNueva", "VersionReglasId", "CorrelacionId", "ActorTipo",
                    "ActorUsuarioId", "ActorAccesoId", "ActorOrganizacionId", "AutorizacionPublicaId",
                    "OrigenTipo", "OrigenId", "Procedencia", "OcurridaEnUtc", "CancelacionEnvioId")
                SELECT c."OperadorId", e."ComercioId", c."EnvioId", 'TransicionEstado', c."EstadoAnterior", 'Cancelado',
                    c."VersionAnterior", c."VersionEnvio", e."VersionReglasId", c."Id",
                    CASE WHEN c."AutorizacionPublicaId" IS NOT NULL THEN 'Publico' ELSE 'Usuario' END,
                    c."UsuarioId", c."AccesoId", c."OrganizacionActorId", c."AutorizacionPublicaId",
                    'CancelacionEnvio', c."Id", 'Backfill', c."CreadaEnUtc", c."Id"
                FROM public."CancelacionesEnvio" c
                JOIN public."Envios" e ON e."Id" = c."EnvioId" AND e."OperadorId" = c."OperadorId"
                ON CONFLICT ("CancelacionEnvioId") WHERE "CancelacionEnvioId" IS NOT NULL DO NOTHING;

                -- ponytail: force the deferred audit FK checks now; PostgreSQL rejects
                -- later ALTER TABLE policy DDL while these backfill trigger events are pending.
                SET CONSTRAINTS "FK_EnvioEventos_CancelacionesEnvio_CancelacionEnvioId" IMMEDIATE;

                ALTER TABLE public."EnvioEventos" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."EnvioEventos" FORCE ROW LEVEL SECURITY;
                CREATE POLICY shipment_history_api_select ON public."EnvioEventos" FOR SELECT TO nexoruta_api
                    USING ((nexoruta_app.setting('actor_kind') = 'comercio' AND "ComercioId" = nexoruta_app.ctx_uuid('org_id'))
                        OR (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id')));
                CREATE POLICY shipment_history_insert_context ON public."EnvioEventos" FOR INSERT TO nexoruta_history_fn
                    WITH CHECK (
                        ("OrigenTipo" = 'CancelacionEnvio' AND "CancelacionEnvioId" = "OrigenId"
                            AND "EnvioId" = nexoruta_app.ctx_uuid('cancel_shipment_id')
                            AND "OperadorId" = nexoruta_app.ctx_uuid('cancel_operator_id'))
                        OR
                        ("OrigenTipo" <> 'CancelacionEnvio' AND "ActorTipo" = 'Usuario'
                            AND "ActorUsuarioId" = nexoruta_app.ctx_uuid('user_id')
                            AND "ActorAccesoId" = nexoruta_app.ctx_uuid('access_id')
                            AND "ActorOrganizacionId" = nexoruta_app.ctx_uuid('org_id')
                            AND ((nexoruta_app.setting('actor_kind') = 'comercio' AND "ComercioId" = nexoruta_app.ctx_uuid('org_id'))
                                OR (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'))))
                        OR
                        ("OrigenTipo" <> 'CancelacionEnvio' AND "ActorTipo" = 'Proceso'
                            AND nexoruta_app.setting('actor_kind') = 'worker'
                            AND "ActorProceso" = nexoruta_app.setting('history_process')
                            AND "OperadorId" = nexoruta_app.ctx_uuid('org_id')
                            AND "EnvioId" = nexoruta_app.ctx_uuid('job_shipment_id'))
                        OR
                        ("OrigenTipo" <> 'CancelacionEnvio' AND "ActorTipo" = 'Proceso'
                            AND nexoruta_app.setting('actor_kind') = 'bootstrap'
                            AND "OperadorId" = nexoruta_app.ctx_uuid('bootstrap_operator_id')
                            AND "ComercioId" = nexoruta_app.ctx_uuid('bootstrap_commerce_id'))
                        OR
                        ("OrigenTipo" <> 'CancelacionEnvio' AND "ActorTipo" = 'Proceso'
                            AND "ActorProceso" = 'database-admin:' || session_user
                            AND EXISTS (SELECT 1 FROM pg_catalog.pg_roles r WHERE r.rolname = session_user AND r.rolsuper))
                    );
                CREATE POLICY shipment_history_fn_cancel_select ON public."EnvioEventos" FOR SELECT TO nexoruta_history_fn
                    USING ("OrigenTipo" = 'CancelacionEnvio' AND "CancelacionEnvioId" IS NOT NULL
                        AND "EnvioId" = nexoruta_app.ctx_uuid('cancel_shipment_id')
                        AND "OperadorId" = nexoruta_app.ctx_uuid('cancel_operator_id'));

                CREATE POLICY shipment_history_fn_source_select ON public."Envios" FOR SELECT TO nexoruta_history_fn
                    USING ("Id" = nexoruta_app.ctx_uuid('cancel_shipment_id')
                        AND "OperadorId" = nexoruta_app.ctx_uuid('cancel_operator_id'));

                CREATE FUNCTION public.capture_shipment_history() RETURNS trigger
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
                AS $capture$
                DECLARE
                    v_actor text := nexoruta_app.setting('actor_kind');
                    v_type text;
                    v_source text := COALESCE(nexoruta_app.setting('history_source_type'), 'Solicitud');
                    v_source_id uuid := COALESCE(nexoruta_app.ctx_uuid('history_source_id'), nexoruta_app.ctx_uuid('history_correlation_id'));
                    v_operation_id uuid := nexoruta_app.ctx_uuid('history_operation_id');
                    v_correlation_id uuid := COALESCE(nexoruta_app.ctx_uuid('history_correlation_id'), nexoruta_app.ctx_uuid('history_source_id'));
                    v_occurred_at timestamptz := COALESCE(NULLIF(nexoruta_app.setting('history_occurred_at'), '')::timestamptz, clock_timestamp());
                    v_actor_type text;
                    v_actor_process text;
                    v_fact text;
                    v_previous text;
                    v_new text;
                    v_observed text;
                    v_version_before bigint;
                    v_version_after bigint;
                    v_actor_user uuid := nexoruta_app.ctx_uuid('user_id');
                    v_actor_access uuid := nexoruta_app.ctx_uuid('access_id');
                    v_actor_org uuid := nexoruta_app.ctx_uuid('org_id');
                    v_public_authorization uuid := nexoruta_app.ctx_uuid('cancel_public_code_id');
                    v_cancel_id uuid;
                    v_session_is_superuser boolean;
                BEGIN
                    SELECT r.rolsuper INTO v_session_is_superuser
                    FROM pg_catalog.pg_roles r WHERE r.rolname = session_user;
                    IF TG_OP = 'INSERT' THEN
                        IF NEW."Estado" <> 'Admitido' OR NEW."Version" <> 0 THEN
                            RAISE EXCEPTION 'Shipment history requires a new shipment to start at Admitido version zero.' USING ERRCODE = '23514';
                        END IF;
                        v_type := 'Creacion'; v_new := NEW."Estado"; v_version_after := NEW."Version";
                    ELSE
                        IF NEW."Estado" IS NOT DISTINCT FROM OLD."Estado" AND NEW."Version" = OLD."Version"
                            AND NEW."EntregaFisicaEnUtc" IS NOT DISTINCT FROM OLD."EntregaFisicaEnUtc" THEN
                            RETURN NEW;
                        END IF;
                        IF NEW."Version" <= OLD."Version" THEN
                            RAISE EXCEPTION 'Shipment state or facts must advance the shipment version.' USING ERRCODE = '23514';
                        END IF;
                        IF NEW."Estado" IS DISTINCT FROM OLD."Estado" THEN
                            IF NEW."Estado" = 'Cancelado' THEN
                                IF v_source = 'CancelacionEnvio' AND v_source_id IS NOT NULL THEN
                                    v_cancel_id := v_source_id;
                                ELSIF NOT COALESCE(v_session_is_superuser, false) THEN
                                    RAISE EXCEPTION 'Cancellation must use the audited cancellation operation.' USING ERRCODE = '42501';
                                END IF;
                            END IF;
                            v_type := 'TransicionEstado'; v_previous := OLD."Estado"; v_new := NEW."Estado";
                        ELSE
                            v_type := 'Hecho'; v_observed := NEW."Estado";
                            v_version_before := OLD."Version";
                            v_fact := COALESCE(nexoruta_app.setting('history_fact_code'), v_source);
                        END IF;
                        v_version_before := OLD."Version"; v_version_after := NEW."Version";
                    END IF;

                    IF v_source = 'CancelacionEnvio' AND v_public_authorization IS NOT NULL THEN
                        v_actor_type := 'Publico';
                        v_actor_user := NULL; v_actor_access := NULL; v_actor_org := NULL;
                    ELSIF v_actor IN ('comercio', 'operador') AND v_actor_user IS NOT NULL AND v_actor_access IS NOT NULL AND v_actor_org IS NOT NULL THEN
                        v_actor_type := 'Usuario';
                    ELSIF v_actor = 'worker' THEN
                        v_actor_type := 'Proceso';
                        v_actor_process := COALESCE(nexoruta_app.setting('history_process'), v_source);
                        v_actor_user := NULL; v_actor_access := NULL; v_actor_org := NULL;
                    ELSIF v_actor = 'bootstrap' THEN
                        v_actor_type := 'Proceso'; v_actor_process := 'bootstrap';
                        v_actor_user := NULL; v_actor_access := NULL; v_actor_org := NULL;
                    ELSIF COALESCE(v_session_is_superuser, false) THEN
                        v_actor_type := 'Proceso'; v_actor_process := 'database-admin:' || session_user;
                        v_actor_user := NULL; v_actor_access := NULL; v_actor_org := NULL;
                    ELSE
                        RAISE EXCEPTION 'Trusted shipment history actor context is missing.' USING ERRCODE = '42501';
                    END IF;

                    INSERT INTO public."EnvioEventos" (
                        "OperadorId", "ComercioId", "EnvioId", "Tipo", "EstadoAnterior", "EstadoNuevo", "EstadoObservado",
                        "VersionAnterior", "VersionNueva", "VersionReglasId", "OperacionJornadaId", "CorrelacionId",
                        "ActorTipo", "ActorUsuarioId", "ActorAccesoId", "ActorOrganizacionId", "ActorProceso", "AutorizacionPublicaId",
                        "OrigenTipo", "OrigenId", "CodigoHecho", "Procedencia", "OcurridaEnUtc", "CancelacionEnvioId")
                    VALUES (
                        NEW."OperadorId", NEW."ComercioId", NEW."Id", v_type, v_previous, v_new, v_observed,
                        v_version_before, v_version_after, NEW."VersionReglasId", v_operation_id, v_correlation_id,
                        v_actor_type, v_actor_user, v_actor_access, v_actor_org, v_actor_process, v_public_authorization,
                        v_source, v_source_id, v_fact, 'EnLinea', v_occurred_at, v_cancel_id);
                    RETURN NEW;
                END $capture$;
                ALTER FUNCTION public.capture_shipment_history() OWNER TO nexoruta_history_fn;
                REVOKE ALL ON FUNCTION public.capture_shipment_history() FROM PUBLIC;
                CREATE TRIGGER shipment_history_capture AFTER INSERT OR UPDATE OF "Estado", "Version", "EntregaFisicaEnUtc"
                    ON public."Envios" FOR EACH ROW EXECUTE FUNCTION public.capture_shipment_history();

                CREATE FUNCTION public.capture_cancellation_history() RETURNS trigger
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
                AS $cancel_history$
                DECLARE v_matches boolean;
                BEGIN
                    SELECT EXISTS (SELECT 1 FROM public."EnvioEventos" h WHERE h."CancelacionEnvioId" = NEW."Id"
                        AND h."EnvioId" = NEW."EnvioId" AND h."OperadorId" = NEW."OperadorId"
                        AND h."EstadoAnterior" = NEW."EstadoAnterior" AND h."VersionAnterior" = NEW."VersionAnterior"
                        AND h."VersionNueva" = NEW."VersionEnvio") INTO v_matches;
                    IF NOT v_matches THEN
                        RAISE EXCEPTION 'Cancellation audit has no matching canonical history transition.' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $cancel_history$;
                ALTER FUNCTION public.capture_cancellation_history() OWNER TO nexoruta_history_fn;
                REVOKE ALL ON FUNCTION public.capture_cancellation_history() FROM PUBLIC;
                CREATE TRIGGER cancellation_history_capture AFTER INSERT ON public."CancelacionesEnvio"
                    FOR EACH ROW EXECUTE FUNCTION public.capture_cancellation_history();

                CREATE FUNCTION public.prevent_shipment_history_mutation() RETURNS trigger
                LANGUAGE plpgsql SET search_path = pg_catalog
                AS $immutable$
                BEGIN
                    RAISE EXCEPTION 'Shipment history is append-only.' USING ERRCODE = '55000';
                END $immutable$;
                ALTER FUNCTION public.prevent_shipment_history_mutation() OWNER TO nexoruta_schema_owner;
                REVOKE ALL ON FUNCTION public.prevent_shipment_history_mutation() FROM PUBLIC;
                CREATE TRIGGER shipment_history_no_update_delete BEFORE UPDATE OR DELETE ON public."EnvioEventos"
                    FOR EACH ROW EXECUTE FUNCTION public.prevent_shipment_history_mutation();
                CREATE TRIGGER shipment_history_no_truncate BEFORE TRUNCATE ON public."EnvioEventos"
                    FOR EACH STATEMENT EXECUTE FUNCTION public.prevent_shipment_history_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $down$
                BEGIN
                    LOCK TABLE public."EnvioEventos" IN ACCESS EXCLUSIVE MODE;
                    IF EXISTS (SELECT 1 FROM public."EnvioEventos") THEN
                        RAISE EXCEPTION 'Canonical shipment history cannot be dropped without destroying audit evidence.' USING ERRCODE = '55000';
                    END IF;
                    DROP TRIGGER IF EXISTS shipment_history_capture ON public."Envios";
                    DROP TRIGGER IF EXISTS cancellation_history_capture ON public."CancelacionesEnvio";
                    DROP TRIGGER IF EXISTS shipment_history_no_update_delete ON public."EnvioEventos";
                    DROP TRIGGER IF EXISTS shipment_history_no_truncate ON public."EnvioEventos";
                    DROP POLICY IF EXISTS shipment_history_fn_source_select ON public."Envios";
                    DROP FUNCTION IF EXISTS public.capture_shipment_history();
                    DROP FUNCTION IF EXISTS public.capture_cancellation_history();
                    DROP FUNCTION IF EXISTS public.prevent_shipment_history_mutation();
                END $down$;
                """);
            migrationBuilder.DropTable(name: "EnvioEventos");
            migrationBuilder.Sql("""
                REVOKE EXECUTE ON FUNCTION nexoruta_app.setting(text), nexoruta_app.ctx_uuid(text) FROM nexoruta_history_fn;
                REVOKE SELECT ON public."Envios" FROM nexoruta_history_fn;
                REVOKE USAGE ON SCHEMA public, nexoruta_app FROM nexoruta_history_fn;
                """);
            // PostgreSQL roles are cluster-scoped and may still own/use objects in another database.
        }
    }
}
