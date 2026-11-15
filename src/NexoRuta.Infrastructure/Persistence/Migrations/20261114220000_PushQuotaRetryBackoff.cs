using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NexoRuta.Infrastructure.Persistence.Migrations;

[DbContext(typeof(NexoRutaDbContext))]
[Migration("20261114220000_PushQuotaRetryBackoff")]
public sealed class PushQuotaRetryBackoff : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
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
                "ProximoIntentoEnUtc" = CASE
                    WHEN p_success THEN m."ProximoIntentoEnUtc"
                    WHEN m."Destino" = 'PushFcm' AND p_error LIKE 'FCM:Cuota:%' THEN v_now + make_interval(secs =>
                        LEAST(3600::double precision,
                            60 * power(2::double precision, LEAST(GREATEST(m."Intentos" - 1, 0), 6))
                            + floor(random() * 12)))
                    WHEN m."Destino" = 'PushFcm' THEN v_now + make_interval(secs =>
                        LEAST(3600::double precision,
                            5 * power(2::double precision, LEAST(m."Intentos", 10))
                            + floor(random() * 15)))
                    ELSE v_now + interval '30 seconds'
                END,
                "UltimoError" = CASE WHEN p_success THEN NULL ELSE left(COALESCE(NULLIF(btrim(p_error), ''), 'Error de publicación'), 2000) END,
                "ProcesadorId" = NULL, "ReservaHastaUtc" = NULL
            WHERE m."Id" = p_message_id AND m."Estado" = 'Procesando'
              AND m."ProcesadorId" = p_processor_id AND m."ReservaHastaUtc" > v_now;
            RETURN FOUND;
        END
        $finish$;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
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
                "ProximoIntentoEnUtc" = CASE
                    WHEN p_success THEN m."ProximoIntentoEnUtc"
                    WHEN m."Destino" = 'PushFcm' THEN v_now + make_interval(secs =>
                        LEAST(3600::double precision, 5 * power(2::double precision, LEAST(m."Intentos", 10)))
                        + floor(random() * 15))
                    ELSE v_now + interval '30 seconds'
                END,
                "UltimoError" = CASE WHEN p_success THEN NULL ELSE left(COALESCE(NULLIF(btrim(p_error), ''), 'Error de publicación'), 2000) END,
                "ProcesadorId" = NULL, "ReservaHastaUtc" = NULL
            WHERE m."Id" = p_message_id AND m."Estado" = 'Procesando'
              AND m."ProcesadorId" = p_processor_id AND m."ReservaHastaUtc" > v_now;
            RETURN FOUND;
        END
        $finish$;
        """);
}
