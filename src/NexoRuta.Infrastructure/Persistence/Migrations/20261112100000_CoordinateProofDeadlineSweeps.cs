using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NexoRuta.Infrastructure.Persistence.Migrations;

[DbContext(typeof(NexoRutaDbContext))]
[Migration("20261112100000_CoordinateProofDeadlineSweeps")]
public sealed class CoordinateProofDeadlineSweeps : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE public."ProofDeadlineJobState" (
            "JobKey" text PRIMARY KEY CHECK ("JobKey" = 'proof-validation-deadline'),
            "LastCompletedAtUtc" timestamp with time zone,
            "NextEligibleAtUtc" timestamp with time zone,
            "MoreWork" boolean NOT NULL DEFAULT false
        );
        ALTER TABLE public."ProofDeadlineJobState" OWNER TO nexoruta_schema_owner;
        ALTER TABLE public."ProofDeadlineJobState" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE public."ProofDeadlineJobState" FORCE ROW LEVEL SECURITY;
        CREATE POLICY proof_deadline_job_state_fn ON public."ProofDeadlineJobState"
            FOR ALL TO nexoruta_proof_deadline_fn USING (true) WITH CHECK (true);
        REVOKE ALL ON public."ProofDeadlineJobState"
            FROM PUBLIC, nexoruta_api, nexoruta_worker, nexoruta_bootstrap;
        GRANT SELECT, INSERT, UPDATE ON public."ProofDeadlineJobState" TO nexoruta_proof_deadline_fn;

        ALTER POLICY shipment_proof_deadline_fn_update ON public."Envios"
            USING (nexoruta_app.setting('actor_kind') = 'worker'
                AND nexoruta_app.setting('history_process') = 'worker:proof-validation-deadline'
                AND "OperadorId" = nexoruta_app.ctx_uuid('org_id')
                AND "Id" = nexoruta_app.ctx_uuid('job_shipment_id')
                AND "Estado" IN ('EntregaPendienteValidacion', 'EntregadoParcialmente', 'EntregaSinValidacion'))
            WITH CHECK ("Estado" IN ('EntregaSinValidacion', 'EntregadoParcialmente'));

        GRANT CREATE ON SCHEMA nexoruta_app TO nexoruta_proof_deadline_fn;
        DROP FUNCTION nexoruta_app.expire_pending_proof_deadlines(timestamptz, integer);
        CREATE FUNCTION nexoruta_app.expire_pending_proof_deadlines(p_now timestamptz, p_batch_size integer)
        RETURNS TABLE("Status" text, "Affected" integer, "MoreWork" boolean)
        LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog AS $function$
        DECLARE
            candidate record;
            shipment_state text;
            changed integer;
            affected_shipment integer;
            total integer := 0;
            more_work boolean := false;
            database_now timestamptz;
            next_eligible timestamptz;
            state_more_work boolean;
        BEGIN
            IF p_now IS NULL OR p_batch_size IS NULL OR p_batch_size NOT BETWEEN 1 AND 500 THEN
                RAISE EXCEPTION 'Invalid deadline sweep input' USING ERRCODE = '22023';
            END IF;

            -- Dedicated NFR-13 lock namespace; transaction scoped so failure/rollback releases it.
            IF NOT pg_catalog.pg_try_advisory_xact_lock(1314018378, 13) THEN
                RETURN QUERY SELECT 'Contended'::text, 0, false;
                RETURN;
            END IF;

            INSERT INTO public."ProofDeadlineJobState" ("JobKey")
            VALUES ('proof-validation-deadline') ON CONFLICT ("JobKey") DO NOTHING;
            SELECT s."NextEligibleAtUtc", s."MoreWork"
              INTO next_eligible, state_more_work
              FROM public."ProofDeadlineJobState" s
             WHERE s."JobKey" = 'proof-validation-deadline'
             FOR UPDATE;
            database_now := pg_catalog.clock_timestamp();
            IF NOT state_more_work AND next_eligible IS NOT NULL AND next_eligible > database_now THEN
                RETURN QUERY SELECT 'NotDue'::text, 0, false;
                RETURN;
            END IF;

            PERFORM pg_catalog.set_config('nexoruta.actor_kind', 'worker', true);
            PERFORM pg_catalog.set_config('nexoruta.history_process', 'worker:proof-validation-deadline:scan', true);
            PERFORM pg_catalog.set_config('nexoruta.proof_deadline_now', p_now::text, true);
            FOR candidate IN
                SELECT e."OperadorId", e."Id"
                  FROM public."Envios" e
                 WHERE e."Estado" IN ('EntregaPendienteValidacion', 'EntregadoParcialmente', 'EntregaSinValidacion')
                   AND (
                       EXISTS (
                           SELECT 1 FROM public."Bultos" b
                            WHERE b."EnvioId" = e."Id" AND b."OperadorId" = e."OperadorId"
                              AND b."Resultado" = 'EntregaPendienteValidacion'
                              AND b."PruebaVenceEnUtc" <= p_now
                       )
                   OR (e."Estado" = 'EntregaPendienteValidacion'
                           AND e."PlazoPruebaVenceEnUtc" <= p_now
                           AND NOT EXISTS (
                               SELECT 1 FROM public."Bultos" b
                                WHERE b."EnvioId" = e."Id" AND b."OperadorId" = e."OperadorId"
                                  AND b."Resultado" <> 'Pendiente'
                           ))
                   )
                 ORDER BY e."Id"
                 LIMIT p_batch_size
            LOOP
                -- Lock the shipment before re-reading the proof state. A competing proof operation
                -- that won this lock first is observed here and is never overwritten by a stale scan.
                -- FOR UPDATE also evaluates the UPDATE policy, so bind this function invocation to
                -- the candidate before acquiring its row lock.
                PERFORM pg_catalog.set_config('nexoruta.history_process', 'worker:proof-validation-deadline', true);
                PERFORM pg_catalog.set_config('nexoruta.org_id', candidate."OperadorId"::text, true);
                PERFORM pg_catalog.set_config('nexoruta.job_shipment_id', candidate."Id"::text, true);
                SELECT e."Estado" INTO shipment_state
                  FROM public."Envios" e
                 WHERE e."OperadorId" = candidate."OperadorId" AND e."Id" = candidate."Id"
                 FOR UPDATE;
                IF NOT FOUND OR shipment_state NOT IN ('EntregaPendienteValidacion', 'EntregadoParcialmente', 'EntregaSinValidacion') THEN
                    PERFORM pg_catalog.set_config('nexoruta.history_process', 'worker:proof-validation-deadline:scan', true);
                    CONTINUE;
                END IF;

                affected_shipment := 0;
                PERFORM pg_catalog.set_config('nexoruta.history_source_type', 'PlazoValidacionPrueba', true);
                PERFORM pg_catalog.set_config('nexoruta.history_source_id', candidate."Id"::text, true);
                PERFORM pg_catalog.set_config('nexoruta.history_correlation_id', candidate."Id"::text, true);
                PERFORM pg_catalog.set_config('nexoruta.history_operation_id', '', true);
                PERFORM pg_catalog.set_config('nexoruta.history_occurred_at', p_now::text, true);
                PERFORM pg_catalog.set_config('nexoruta.history_fact_code', 'PruebaBultoVencida', true);

                UPDATE public."Bultos"
                   SET "Resultado" = 'EntregaSinValidacion', "Version" = "Version" + 1
                 WHERE "OperadorId" = candidate."OperadorId" AND "EnvioId" = candidate."Id"
                   AND "Resultado" = 'EntregaPendienteValidacion' AND "PruebaVenceEnUtc" <= p_now;
                GET DIAGNOSTICS changed = ROW_COUNT;
                IF changed > 0 THEN
                    -- A later parcel can expire after the shipment state was already resolved.
                    -- Its real change still advances the shipment version and records the fact.
                    UPDATE public."Envios"
                       SET "Estado" = CASE WHEN "Estado" = 'EntregadoParcialmente'
                                           THEN "Estado" ELSE 'EntregaSinValidacion' END,
                           "Version" = "Version" + 1
                     WHERE "OperadorId" = candidate."OperadorId" AND "Id" = candidate."Id"
                       AND "Estado" IN ('EntregaPendienteValidacion', 'EntregadoParcialmente', 'EntregaSinValidacion');
                    GET DIAGNOSTICS affected_shipment = ROW_COUNT;
                ELSIF shipment_state = 'EntregaPendienteValidacion'
                      AND EXISTS (
                          SELECT 1 FROM public."Envios" e
                           WHERE e."OperadorId" = candidate."OperadorId" AND e."Id" = candidate."Id"
                             AND e."PlazoPruebaVenceEnUtc" <= p_now
                             AND NOT EXISTS (
                                 SELECT 1 FROM public."Bultos" b
                                  WHERE b."OperadorId" = candidate."OperadorId"
                                    AND b."EnvioId" = candidate."Id" AND b."Resultado" <> 'Pendiente'
                             )
                      ) THEN
                    UPDATE public."Envios"
                       SET "Estado" = 'EntregaSinValidacion', "Version" = "Version" + 1
                     WHERE "OperadorId" = candidate."OperadorId" AND "Id" = candidate."Id"
                       AND "Estado" = 'EntregaPendienteValidacion';
                    GET DIAGNOSTICS affected_shipment = ROW_COUNT;
                END IF;
                total := total + affected_shipment;
                PERFORM pg_catalog.set_config('nexoruta.history_process', 'worker:proof-validation-deadline:scan', true);
            END LOOP;

            SELECT EXISTS (
                SELECT 1 FROM public."Envios" e
                 WHERE e."Estado" IN ('EntregaPendienteValidacion', 'EntregadoParcialmente', 'EntregaSinValidacion')
                   AND (
                       EXISTS (
                           SELECT 1 FROM public."Bultos" b
                            WHERE b."EnvioId" = e."Id" AND b."OperadorId" = e."OperadorId"
                              AND b."Resultado" = 'EntregaPendienteValidacion'
                              AND b."PruebaVenceEnUtc" <= p_now
                       )
                       OR (e."Estado" = 'EntregaPendienteValidacion'
                           AND e."PlazoPruebaVenceEnUtc" <= p_now
                           AND NOT EXISTS (
                               SELECT 1 FROM public."Bultos" b
                                WHERE b."EnvioId" = e."Id" AND b."OperadorId" = e."OperadorId"
                                  AND b."Resultado" <> 'Pendiente'
                           ))
                   )
            ) INTO more_work;

            database_now := pg_catalog.clock_timestamp();
            UPDATE public."ProofDeadlineJobState"
               SET "LastCompletedAtUtc" = database_now,
                   "NextEligibleAtUtc" = database_now + interval '30 seconds',
                   "MoreWork" = more_work
             WHERE "JobKey" = 'proof-validation-deadline';
            RETURN QUERY SELECT 'Completed'::text, total, more_work;
        END
        $function$;
        ALTER FUNCTION nexoruta_app.expire_pending_proof_deadlines(timestamptz, integer)
            OWNER TO nexoruta_proof_deadline_fn;
        REVOKE CREATE ON SCHEMA nexoruta_app FROM nexoruta_proof_deadline_fn;
        REVOKE ALL ON FUNCTION nexoruta_app.expire_pending_proof_deadlines(timestamptz, integer)
            FROM PUBLIC, nexoruta_api, nexoruta_bootstrap;
        GRANT EXECUTE ON FUNCTION nexoruta_app.expire_pending_proof_deadlines(timestamptz, integer)
            TO nexoruta_worker;
        """);

    protected override void Down(MigrationBuilder migrationBuilder)
        => throw new NotSupportedException("Rolling this migration back would restore an unsafe deadline sweep contract.");
}
