using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace NexoRuta.Infrastructure.Persistence.Migrations;
[DbContext(typeof(NexoRutaDbContext))]
[Migration("20261102170000_ExposeCommerceCoverage")]
public sealed class ExposeCommerceCoverage : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE FUNCTION nexoruta_app.shipment_coverage(p_operator uuid)
        RETURNS text LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,nexoruta_app AS $$
        DECLARE result text;
        BEGIN
            PERFORM nexoruta_app.shipment_rules_for_admission(p_operator);
            SELECT coalesce(jsonb_agg(jsonb_build_object('ZonaId',z."Id",'Nombre',z."Nombre",
                'Geometria',public.st_asgeojson(z."Geometria")) ORDER BY z."Id"),'[]'::jsonb)::text
            INTO result FROM public."ZonasCobertura" z
            WHERE z."OperadorId"=p_operator AND z."Estado"='Activa';
            RETURN result;
        END $$;
        ALTER FUNCTION nexoruta_app.shipment_coverage(uuid) OWNER TO nexoruta_shipment_fn;
        REVOKE ALL ON FUNCTION nexoruta_app.shipment_coverage(uuid) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION nexoruta_app.shipment_coverage(uuid) TO nexoruta_api;
        """);
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("DROP FUNCTION nexoruta_app.shipment_coverage(uuid);");
}
