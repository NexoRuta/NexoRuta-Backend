using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NexoRuta.Infrastructure.Persistence;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations;

[DbContext(typeof(NexoRutaDbContext))]
[Migration("20261030120000_ListImportedShipmentReferences")]
public sealed class ListImportedShipmentReferences : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE FUNCTION nexoruta_app.list_import_shipment_references(p_shipment_ids uuid[])
            RETURNS TABLE("EnvioId" uuid, "ReferenciaExterna" text)
            LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
            AS $list_import_references$
            DECLARE v_commerce_id uuid := nexoruta_app.ctx_uuid('org_id');
                    v_user_id uuid := nexoruta_app.ctx_uuid('user_id');
            BEGIN
                IF nexoruta_app.setting('actor_kind') IS DISTINCT FROM 'comercio' OR v_commerce_id IS NULL
                    OR v_user_id IS NULL OR nexoruta_app.ctx_uuid('access_id') IS NULL
                    OR NOT EXISTS (SELECT 1 FROM public."AccesosUsuario" a WHERE a."Id" = nexoruta_app.ctx_uuid('access_id')
                        AND a."UsuarioId" = v_user_id AND a."ComercioId" = v_commerce_id AND a."Activo" AND a."RolId" IS NOT NULL)
                    OR NOT EXISTS (SELECT 1 FROM public."Comercios" c WHERE c."Id" = v_commerce_id AND c."Estado" = 'Activa') THEN
                    RAISE EXCEPTION 'Import identity list is not authorized.' USING ERRCODE = '42501';
                END IF;

                RETURN QUERY
                SELECT i."EnvioId", i."ReferenciaExterna"::text
                FROM public."IdentidadesEnvioImportado" i
                WHERE i."ComercioId" = v_commerce_id AND i."EnvioId" = ANY(p_shipment_ids);
            END
            $list_import_references$;
            ALTER FUNCTION nexoruta_app.list_import_shipment_references(uuid[]) OWNER TO nexoruta_shipment_fn;
            REVOKE ALL ON FUNCTION nexoruta_app.list_import_shipment_references(uuid[]) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION nexoruta_app.list_import_shipment_references(uuid[]) TO nexoruta_api;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP FUNCTION nexoruta_app.list_import_shipment_references(uuid[]);");
    }
}
