namespace NexoRuta.Infrastructure.Persistence.Migrations;

internal static class SecurityOutboxMigrationSql
{
    // ponytail: a dedicated security table keeps global recovery work out of shipment-scoped RLS and foreign keys.
    public const string Up = """
        ALTER TABLE public."SecurityOutboxMessages" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE public."SecurityOutboxMessages" FORCE ROW LEVEL SECURITY;
        ALTER TABLE public."SecurityOutboxMessages" OWNER TO nexoruta_schema_owner;
        REVOKE ALL ON public."SecurityOutboxMessages" FROM PUBLIC, nexoruta_api, nexoruta_worker,
            nexoruta_bootstrap, nexoruta_queue_fn;
        GRANT INSERT ON public."SecurityOutboxMessages" TO nexoruta_api;
        GRANT SELECT, UPDATE ON public."SecurityOutboxMessages" TO nexoruta_queue_fn;

        CREATE POLICY security_outbox_api_insert ON public."SecurityOutboxMessages"
            FOR INSERT TO nexoruta_api WITH CHECK (
                "State" = 'Pending' AND "Attempts" = 0 AND "ProcessorId" IS NULL
                AND "LeaseUntilUtc" IS NULL AND "PublishedAtUtc" IS NULL AND "LastError" IS NULL
                AND (("Type" = 'PasswordRecoveryEmail' AND "Destination" = 'RabbitMq' AND "DeliveryState" = 'Pending')
                  OR ("Type" = 'PasswordRecoveryRequested' AND "Destination" = 'Kafka' AND "DeliveryState" IS NULL))
            );
        CREATE POLICY security_outbox_queue_select ON public."SecurityOutboxMessages"
            FOR SELECT TO nexoruta_queue_fn USING (
                nexoruta_app.setting('security_outbox_mode') IN ('claim', 'finish', 'delivery', 'delivery-read')
            );
        CREATE POLICY security_outbox_queue_update ON public."SecurityOutboxMessages"
            FOR UPDATE TO nexoruta_queue_fn
            USING (
                nexoruta_app.setting('security_outbox_mode') = 'claim'
                OR ("Id" = nexoruta_app.ctx_uuid('security_outbox_message_id')
                    AND nexoruta_app.setting('security_outbox_mode') IN ('finish', 'delivery'))
            )
            WITH CHECK (
                (nexoruta_app.setting('security_outbox_mode') = 'claim'
                    AND "ProcessorId" = nexoruta_app.ctx_uuid('security_outbox_processor_id'))
                OR ("Id" = nexoruta_app.ctx_uuid('security_outbox_message_id')
                    AND "ProcessorId" IS NULL
                    AND nexoruta_app.setting('security_outbox_mode') = 'finish')
                OR ("Id" = nexoruta_app.ctx_uuid('security_outbox_message_id')
                    AND nexoruta_app.setting('security_outbox_mode') = 'delivery')
            );

        CREATE OR REPLACE FUNCTION nexoruta_app.claim_security_outbox(p_processor_id uuid, p_destination text)
        RETURNS TABLE("Id" uuid, "Type" text, "Destination" text, "Payload" text,
                      "Attempts" integer, "CreatedAtUtc" timestamptz)
        LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
        AS $claim$
        DECLARE v_id uuid; v_now timestamptz := clock_timestamp();
        BEGIN
            IF p_processor_id IS NULL OR p_processor_id = '00000000-0000-0000-0000-000000000000'::uuid
               OR p_destination NOT IN ('RabbitMq', 'Kafka') THEN RAISE EXCEPTION 'Invalid security outbox claim.'; END IF;
            PERFORM set_config('nexoruta.security_outbox_mode', 'claim', true);
            PERFORM set_config('nexoruta.security_outbox_processor_id', p_processor_id::text, true);
            SELECT m."Id" INTO v_id FROM public."SecurityOutboxMessages" m
             WHERE m."Destination" = p_destination AND (
                 (m."State" IN ('Pending', 'Error') AND m."NextAttemptAtUtc" <= v_now) OR
                 (m."State" = 'Processing' AND m."LeaseUntilUtc" <= v_now))
             ORDER BY m."CreatedAtUtc" FOR UPDATE SKIP LOCKED LIMIT 1;
            IF v_id IS NULL THEN RETURN; END IF;
            RETURN QUERY
            UPDATE public."SecurityOutboxMessages" m SET "State" = 'Processing',
                   "ProcessorId" = p_processor_id, "LeaseUntilUtc" = v_now + interval '2 minutes',
                   "Attempts" = CASE WHEN m."Attempts" < 2147483647 THEN m."Attempts" + 1 ELSE m."Attempts" END
             WHERE m."Id" = v_id
            RETURNING m."Id", m."Type"::text, m."Destination"::text, m."Payload",
                      m."Attempts", m."CreatedAtUtc";
        END
        $claim$;

        CREATE OR REPLACE FUNCTION nexoruta_app.finish_security_outbox(
            p_message_id uuid, p_processor_id uuid, p_published boolean, p_error text DEFAULT NULL)
        RETURNS boolean
        LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
        AS $finish$
        DECLARE v_now timestamptz := clock_timestamp();
        BEGIN
            IF p_message_id IS NULL OR p_processor_id IS NULL THEN RETURN false; END IF;
            PERFORM set_config('nexoruta.security_outbox_mode', 'finish', true);
            PERFORM set_config('nexoruta.security_outbox_message_id', p_message_id::text, true);
            PERFORM set_config('nexoruta.security_outbox_processor_id', p_processor_id::text, true);
            UPDATE public."SecurityOutboxMessages" m SET
                "State" = CASE WHEN p_published THEN 'Published' ELSE 'Error' END,
                "PublishedAtUtc" = CASE WHEN p_published THEN v_now ELSE m."PublishedAtUtc" END,
                "NextAttemptAtUtc" = CASE WHEN p_published THEN m."NextAttemptAtUtc" ELSE v_now + interval '30 seconds' END,
                "LastError" = CASE WHEN p_published THEN NULL ELSE left(COALESCE(NULLIF(btrim(p_error), ''), 'Publication failed'), 200) END,
                "ProcessorId" = NULL, "LeaseUntilUtc" = NULL
            WHERE m."Id" = p_message_id AND m."State" = 'Processing'
              AND m."ProcessorId" = p_processor_id AND m."LeaseUntilUtc" > v_now;
            RETURN FOUND;
        END
        $finish$;

        CREATE OR REPLACE FUNCTION nexoruta_app.mark_password_recovery_delivery(p_message_id uuid, p_state text)
        RETURNS boolean
        LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
        AS $delivery$
        BEGIN
            IF p_message_id IS NULL OR p_state NOT IN ('Delivered', 'Expired', 'Failed') THEN RETURN false; END IF;
            PERFORM set_config('nexoruta.security_outbox_mode', 'delivery', true);
            PERFORM set_config('nexoruta.security_outbox_message_id', p_message_id::text, true);
            UPDATE public."SecurityOutboxMessages" m SET "DeliveryState" = p_state
            WHERE m."Id" = p_message_id AND m."Type" = 'PasswordRecoveryEmail'
              AND m."Destination" = 'RabbitMq' AND m."DeliveryState" = 'Pending';
            RETURN FOUND;
        END
        $delivery$;

        CREATE OR REPLACE FUNCTION nexoruta_app.get_password_recovery_delivery_state(p_message_id uuid)
        RETURNS text
        LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
        AS $delivery_state$
        DECLARE v_state text;
        BEGIN
            IF p_message_id IS NULL THEN RETURN NULL; END IF;
            PERFORM set_config('nexoruta.security_outbox_mode', 'delivery-read', true);
            SELECT m."DeliveryState" INTO v_state FROM public."SecurityOutboxMessages" m
             WHERE m."Id" = p_message_id AND m."Type" = 'PasswordRecoveryEmail'
               AND m."Destination" = 'RabbitMq';
            RETURN v_state;
        END
        $delivery_state$;

        ALTER FUNCTION nexoruta_app.claim_security_outbox(uuid, text) OWNER TO nexoruta_queue_fn;
        ALTER FUNCTION nexoruta_app.finish_security_outbox(uuid, uuid, boolean, text) OWNER TO nexoruta_queue_fn;
        ALTER FUNCTION nexoruta_app.mark_password_recovery_delivery(uuid, text) OWNER TO nexoruta_queue_fn;
        ALTER FUNCTION nexoruta_app.get_password_recovery_delivery_state(uuid) OWNER TO nexoruta_queue_fn;
        REVOKE ALL ON FUNCTION nexoruta_app.claim_security_outbox(uuid, text),
            nexoruta_app.finish_security_outbox(uuid, uuid, boolean, text),
            nexoruta_app.mark_password_recovery_delivery(uuid, text),
            nexoruta_app.get_password_recovery_delivery_state(uuid) FROM PUBLIC, nexoruta_api, nexoruta_worker;
        GRANT EXECUTE ON FUNCTION nexoruta_app.claim_security_outbox(uuid, text),
            nexoruta_app.finish_security_outbox(uuid, uuid, boolean, text),
            nexoruta_app.mark_password_recovery_delivery(uuid, text),
            nexoruta_app.get_password_recovery_delivery_state(uuid) TO nexoruta_worker;
        """;

    public const string Down = """
        DROP FUNCTION IF EXISTS nexoruta_app.get_password_recovery_delivery_state(uuid);
        DROP FUNCTION IF EXISTS nexoruta_app.mark_password_recovery_delivery(uuid, text);
        DROP FUNCTION IF EXISTS nexoruta_app.finish_security_outbox(uuid, uuid, boolean, text);
        DROP FUNCTION IF EXISTS nexoruta_app.claim_security_outbox(uuid, text);
        """;
}
