using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HistoricalImportOperatorCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE POLICY shipment_fn_operator_import_history_select ON public."Operadores" FOR SELECT TO nexoruta_shipment_fn
                    USING (nexoruta_app.setting('actor_kind') = 'comercio'
                        AND EXISTS (SELECT 1 FROM public."IdentidadesEnvioImportado" i
                            WHERE i."ComercioId" = nexoruta_app.ctx_uuid('org_id')
                                AND i."OperadorId" = "Operadores"."Id"));

                CREATE FUNCTION nexoruta_app.list_import_operator_history()
                RETURNS TABLE("OperadorId" uuid, "Nombre" text)
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
                AS $list_import_operators$
                DECLARE v_commerce_id uuid := nexoruta_app.ctx_uuid('org_id');
                        v_user_id uuid := nexoruta_app.ctx_uuid('user_id');
                BEGIN
                    IF nexoruta_app.setting('actor_kind') IS DISTINCT FROM 'comercio' OR v_commerce_id IS NULL
                        OR v_user_id IS NULL OR nexoruta_app.ctx_uuid('access_id') IS NULL
                        OR NOT EXISTS (SELECT 1 FROM public."AccesosUsuario" a WHERE a."Id" = nexoruta_app.ctx_uuid('access_id')
                            AND a."UsuarioId" = v_user_id AND a."ComercioId" = v_commerce_id AND a."Activo" AND a."RolId" IS NOT NULL)
                        OR NOT EXISTS (SELECT 1 FROM public."Comercios" c WHERE c."Id" = v_commerce_id AND c."Estado" = 'Activa') THEN
                        RAISE EXCEPTION 'Import operator history is not authorized.' USING ERRCODE = '42501';
                    END IF;

                    RETURN QUERY
                    SELECT o."Id", o."Nombre"::text
                    FROM public."Operadores" o
                    WHERE o."Estado" <> 'Activa'
                        AND EXISTS (SELECT 1 FROM public."IdentidadesEnvioImportado" i
                            WHERE i."ComercioId" = v_commerce_id AND i."OperadorId" = o."Id")
                    ORDER BY o."Nombre" COLLATE "C", o."Id";
                END
                $list_import_operators$;
                ALTER FUNCTION nexoruta_app.list_import_operator_history() OWNER TO nexoruta_shipment_fn;
                REVOKE ALL ON FUNCTION nexoruta_app.list_import_operator_history() FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION nexoruta_app.list_import_operator_history() TO nexoruta_api;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION nexoruta_app.list_import_operator_history();
                DROP POLICY shipment_fn_operator_import_history_select ON public."Operadores";
                """);
        }
    }
}
