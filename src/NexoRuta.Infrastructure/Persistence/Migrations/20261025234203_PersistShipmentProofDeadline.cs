using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersistShipmentProofDeadline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EntregaFisicaRecibidaEnUtc",
                table: "Envios",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PlazoPruebaVenceEnUtc",
                table: "Envios",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Envios_ProofValidationDeadline",
                table: "Envios",
                column: "PlazoPruebaVenceEnUtc",
                filter: "\"Estado\" = 'EntregaPendienteValidacion' AND \"PlazoPruebaVenceEnUtc\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Envios_ProofDeadlineRequiresPhysicalDelivery",
                table: "Envios",
                sql: "\"EntregaFisicaRecibidaEnUtc\" IS NULL OR \"EntregaFisicaEnUtc\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Envios_ProofDeadlineRequiresReceipt",
                table: "Envios",
                sql: "\"PlazoPruebaVenceEnUtc\" IS NULL OR (\"EntregaFisicaRecibidaEnUtc\" IS NOT NULL AND \"PlazoPruebaVenceEnUtc\" > \"EntregaFisicaRecibidaEnUtc\")");

            migrationBuilder.Sql("""
                DO $roles$
                DECLARE v_role oid;
                BEGIN
                    SELECT oid INTO v_role FROM pg_catalog.pg_roles WHERE rolname = 'nexoruta_proof_deadline_fn';
                    IF v_role IS NULL THEN
                        CREATE ROLE nexoruta_proof_deadline_fn NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
                    ELSIF EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE oid = v_role
                        AND (rolcanlogin OR rolinherit OR rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls))
                        OR EXISTS (SELECT 1 FROM pg_catalog.pg_auth_members WHERE roleid = v_role OR member = v_role) THEN
                        RAISE EXCEPTION 'Role nexoruta_proof_deadline_fn already exists with unsafe attributes or memberships.'
                            USING ERRCODE = '42501';
                    END IF;
                END $roles$;

                CREATE FUNCTION nexoruta_app.guard_shipment_proof_deadline() RETURNS trigger
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public
                AS $guard$
                BEGIN
                    IF OLD."EntregaFisicaEnUtc" IS NOT NULL AND (
                        NEW."EntregaFisicaEnUtc" IS DISTINCT FROM OLD."EntregaFisicaEnUtc"
                        OR NEW."EntregaFisicaRecibidaEnUtc" IS DISTINCT FROM OLD."EntregaFisicaRecibidaEnUtc"
                        OR NEW."PlazoPruebaVenceEnUtc" IS DISTINCT FROM OLD."PlazoPruebaVenceEnUtc") THEN
                        RAISE EXCEPTION 'Accepted physical-delivery timestamps and proof deadline are immutable'
                            USING ERRCODE = '55000';
                    END IF;
                    IF OLD."EntregaFisicaEnUtc" IS NULL AND NEW."EntregaFisicaEnUtc" IS NOT NULL THEN
                        IF NEW."EntregaFisicaRecibidaEnUtc" IS NULL THEN
                            RAISE EXCEPTION 'A physical-delivery declaration requires its first server receipt time'
                                USING ERRCODE = '23514';
                        END IF;
                        IF NEW."Estado" = 'Cancelado' AND NEW."PlazoPruebaVenceEnUtc" IS NOT NULL THEN
                            RAISE EXCEPTION 'A late physical fact on a cancelled shipment cannot start proof validation'
                                USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    RETURN NEW;
                END
                $guard$;
                ALTER FUNCTION nexoruta_app.guard_shipment_proof_deadline() OWNER TO nexoruta_schema_owner;
                REVOKE ALL ON FUNCTION nexoruta_app.guard_shipment_proof_deadline()
                    FROM PUBLIC, nexoruta_api, nexoruta_worker, nexoruta_bootstrap;
                CREATE TRIGGER shipment_proof_deadline_guard
                    BEFORE UPDATE OF "EntregaFisicaEnUtc", "EntregaFisicaRecibidaEnUtc", "PlazoPruebaVenceEnUtc"
                    ON public."Envios" FOR EACH ROW EXECUTE FUNCTION nexoruta_app.guard_shipment_proof_deadline();

                GRANT UPDATE ("EntregaFisicaRecibidaEnUtc", "PlazoPruebaVenceEnUtc") ON public."Envios" TO nexoruta_api;
                GRANT USAGE ON SCHEMA public, nexoruta_app TO nexoruta_proof_deadline_fn;
                GRANT CREATE ON SCHEMA nexoruta_app TO nexoruta_proof_deadline_fn;
                GRANT EXECUTE ON FUNCTION nexoruta_app.setting(text), nexoruta_app.ctx_uuid(text) TO nexoruta_proof_deadline_fn;
                GRANT SELECT ON public."Envios" TO nexoruta_proof_deadline_fn;
                GRANT UPDATE ("Estado", "Version") ON public."Envios" TO nexoruta_proof_deadline_fn;

                CREATE POLICY shipment_proof_deadline_fn_scan ON public."Envios" FOR SELECT TO nexoruta_proof_deadline_fn
                    USING (nexoruta_app.setting('actor_kind') = 'worker'
                        AND ((nexoruta_app.setting('history_process') = 'worker:proof-validation-deadline:scan'
                                AND "Estado" = 'EntregaPendienteValidacion' AND "PlazoPruebaVenceEnUtc" IS NOT NULL)
                            OR (nexoruta_app.setting('history_process') = 'worker:proof-validation-deadline'
                                AND "OperadorId" = nexoruta_app.ctx_uuid('org_id')
                                AND "Id" = nexoruta_app.ctx_uuid('job_shipment_id')
                                AND "Estado" IN ('EntregaPendienteValidacion', 'EntregaSinValidacion')
                                AND "PlazoPruebaVenceEnUtc" <= NULLIF(nexoruta_app.setting('proof_deadline_now'), '')::timestamptz)));
                CREATE POLICY shipment_proof_deadline_fn_update ON public."Envios" FOR UPDATE TO nexoruta_proof_deadline_fn
                    USING (nexoruta_app.setting('actor_kind') = 'worker'
                        AND nexoruta_app.setting('history_process') = 'worker:proof-validation-deadline'
                        AND "OperadorId" = nexoruta_app.ctx_uuid('org_id')
                        AND "Id" = nexoruta_app.ctx_uuid('job_shipment_id')
                        AND "Estado" = 'EntregaPendienteValidacion'
                        AND "PlazoPruebaVenceEnUtc" <= NULLIF(nexoruta_app.setting('proof_deadline_now'), '')::timestamptz)
                    -- OperatorId is not updateable by this function role; USING binds the old row to this operator/shipment.
                    WITH CHECK (nexoruta_app.setting('actor_kind') = 'worker'
                        AND nexoruta_app.setting('history_process') = 'worker:proof-validation-deadline'
                        AND "Id" = nexoruta_app.ctx_uuid('job_shipment_id')
                        AND "Estado" = 'EntregaSinValidacion');

                CREATE FUNCTION nexoruta_app.expire_pending_proof_deadlines(p_now timestamptz, p_batch_size integer)
                RETURNS integer LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
                AS $expire$
                DECLARE
                    candidate record;
                    changed integer;
                    total integer := 0;
                BEGIN
                    IF p_now IS NULL OR p_batch_size NOT BETWEEN 1 AND 500 THEN
                        RAISE EXCEPTION 'Invalid proof-deadline sweep input' USING ERRCODE = '22023';
                    END IF;
                    PERFORM set_config('nexoruta.actor_kind', 'worker', true);
                    PERFORM set_config('nexoruta.history_process', 'worker:proof-validation-deadline:scan', true);
                    PERFORM set_config('nexoruta.org_id', '', true);
                    PERFORM set_config('nexoruta.job_shipment_id', '', true);
                    PERFORM set_config('nexoruta.proof_deadline_now', p_now::text, true);

                    FOR candidate IN
                        SELECT e."OperadorId", e."Id"
                          FROM public."Envios" e
                         WHERE e."Estado" = 'EntregaPendienteValidacion'
                           AND e."PlazoPruebaVenceEnUtc" IS NOT NULL
                           AND e."PlazoPruebaVenceEnUtc" <= p_now
                         ORDER BY e."PlazoPruebaVenceEnUtc", e."OperadorId", e."Id"
                         LIMIT p_batch_size
                    LOOP
                        PERFORM set_config('nexoruta.history_process', 'worker:proof-validation-deadline', true);
                        PERFORM set_config('nexoruta.org_id', candidate."OperadorId"::text, true);
                        PERFORM set_config('nexoruta.job_shipment_id', candidate."Id"::text, true);
                        PERFORM set_config('nexoruta.history_source_type', 'PlazoValidacionPrueba', true);
                        PERFORM set_config('nexoruta.history_source_id', candidate."Id"::text, true);
                        PERFORM set_config('nexoruta.history_operation_id', '', true);
                        PERFORM set_config('nexoruta.history_correlation_id', candidate."Id"::text, true);
                        PERFORM set_config('nexoruta.history_occurred_at', p_now::text, true);
                        PERFORM set_config('nexoruta.history_fact_code', '', true);

                        UPDATE public."Envios"
                           SET "Estado" = 'EntregaSinValidacion', "Version" = "Version" + 1
                         WHERE "OperadorId" = candidate."OperadorId" AND "Id" = candidate."Id"
                           AND "Estado" = 'EntregaPendienteValidacion'
                           AND "PlazoPruebaVenceEnUtc" IS NOT NULL
                           AND "PlazoPruebaVenceEnUtc" <= p_now;
                        GET DIAGNOSTICS changed = ROW_COUNT;
                        total := total + changed;
                        PERFORM set_config('nexoruta.history_process', 'worker:proof-validation-deadline:scan', true);
                    END LOOP;
                    RETURN total;
                END
                $expire$;
                ALTER FUNCTION nexoruta_app.expire_pending_proof_deadlines(timestamptz, integer)
                    OWNER TO nexoruta_proof_deadline_fn;
                REVOKE CREATE ON SCHEMA nexoruta_app FROM nexoruta_proof_deadline_fn;
                REVOKE ALL ON FUNCTION nexoruta_app.expire_pending_proof_deadlines(timestamptz, integer)
                    FROM PUBLIC, nexoruta_api, nexoruta_bootstrap;
                GRANT EXECUTE ON FUNCTION nexoruta_app.expire_pending_proof_deadlines(timestamptz, integer) TO nexoruta_worker;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS nexoruta_app.expire_pending_proof_deadlines(timestamptz, integer);
                DROP POLICY IF EXISTS shipment_proof_deadline_fn_scan ON public."Envios";
                DROP POLICY IF EXISTS shipment_proof_deadline_fn_update ON public."Envios";
                REVOKE UPDATE ("EntregaFisicaRecibidaEnUtc", "PlazoPruebaVenceEnUtc") ON public."Envios" FROM nexoruta_api;
                REVOKE ALL ON public."Envios" FROM nexoruta_proof_deadline_fn;
                REVOKE EXECUTE ON FUNCTION nexoruta_app.setting(text), nexoruta_app.ctx_uuid(text) FROM nexoruta_proof_deadline_fn;
                REVOKE CREATE ON SCHEMA nexoruta_app FROM nexoruta_proof_deadline_fn;
                REVOKE USAGE ON SCHEMA public, nexoruta_app FROM nexoruta_proof_deadline_fn;
                DROP TRIGGER IF EXISTS shipment_proof_deadline_guard ON public."Envios";
                DROP FUNCTION IF EXISTS nexoruta_app.guard_shipment_proof_deadline();
                """);

            migrationBuilder.DropIndex(
                name: "IX_Envios_ProofValidationDeadline",
                table: "Envios");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Envios_ProofDeadlineRequiresPhysicalDelivery",
                table: "Envios");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Envios_ProofDeadlineRequiresReceipt",
                table: "Envios");

            migrationBuilder.DropColumn(
                name: "EntregaFisicaRecibidaEnUtc",
                table: "Envios");

            migrationBuilder.DropColumn(
                name: "PlazoPruebaVenceEnUtc",
                table: "Envios");
        }
    }
}
