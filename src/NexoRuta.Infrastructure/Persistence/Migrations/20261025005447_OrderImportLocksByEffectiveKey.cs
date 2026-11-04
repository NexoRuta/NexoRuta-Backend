using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrderImportLocksByEffectiveKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION nexoruta_app.lock_import_references(p_references text[]) RETURNS void
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
                AS $lock_import$
                DECLARE v_commerce_id uuid := nexoruta_app.ctx_uuid('org_id');
                        v_user_id uuid := nexoruta_app.ctx_uuid('user_id');
                        v_lock_key bigint;
                BEGIN
                    IF nexoruta_app.setting('actor_kind') IS DISTINCT FROM 'comercio' OR v_commerce_id IS NULL
                        OR v_user_id IS NULL OR nexoruta_app.ctx_uuid('access_id') IS NULL
                        OR NOT EXISTS (SELECT 1 FROM public."AccesosUsuario" a WHERE a."Id" = nexoruta_app.ctx_uuid('access_id')
                            AND a."UsuarioId" = v_user_id AND a."ComercioId" = v_commerce_id AND a."Activo" AND a."RolId" IS NOT NULL)
                        OR NOT EXISTS (SELECT 1 FROM public."Comercios" c WHERE c."Id" = v_commerce_id AND c."Estado" = 'Activa') THEN
                        RAISE EXCEPTION 'Import identity lock is not authorized.' USING ERRCODE = '42501';
                    END IF;
                    IF p_references IS NULL OR cardinality(p_references) > 500
                        OR EXISTS (SELECT 1 FROM unnest(p_references) AS refs(value)
                            WHERE value IS NULL OR char_length(value) NOT BETWEEN 1 AND 128 OR value ~ '[[:cntrl:]]') THEN
                        RAISE EXCEPTION 'Import references are invalid.' USING ERRCODE = '22023';
                    END IF;

                    -- ponytail: serialize by distinct effective bigint keys, not pre-hash reference text.
                    FOR v_lock_key IN
                        SELECT DISTINCT pg_catalog.hashtextextended(
                            v_commerce_id::text || ':' || char_length(refs.value)::text || ':' || refs.value, 0) AS lock_key
                        FROM unnest(p_references) AS refs(value)
                        ORDER BY lock_key
                    LOOP
                        PERFORM pg_catalog.pg_advisory_xact_lock(v_lock_key);
                    END LOOP;
                END
                $lock_import$;
                ALTER FUNCTION nexoruta_app.lock_import_references(text[]) OWNER TO nexoruta_shipment_fn;
                REVOKE ALL ON FUNCTION nexoruta_app.lock_import_references(text[]) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION nexoruta_app.lock_import_references(text[]) TO nexoruta_api;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally retain the safe ordering: reverting this additive fix must not restore deadlock-prone locking.
        }
    }
}
