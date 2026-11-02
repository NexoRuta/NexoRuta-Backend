using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceFleetOccupationExclusivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $authorized$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname=current_user
                        AND (rolsuper OR rolbypassrls OR (rolcreaterole
                            AND pg_has_role(current_user,'nexoruta_schema_owner','USAGE')))) THEN
                        RAISE EXCEPTION 'Fleet activation requires an authorized migration identity able to inspect all tenants.';
                    END IF;
                    IF EXISTS (SELECT 1 FROM pg_policy
                        WHERE polrelid='public."OcupacionesRuta"'::regclass AND NOT polpermissive) THEN
                        RAISE EXCEPTION 'Fleet activation cannot prove complete visibility with restrictive occupation policies.';
                    END IF;
                END $authorized$;
                LOCK TABLE public."OcupacionesRuta" IN SHARE ROW EXCLUSIVE MODE;
                CREATE POLICY migration_fleet_activation_read ON public."OcupacionesRuta"
                    FOR SELECT TO CURRENT_USER USING (true);
                DO $activation$ BEGIN
                    IF EXISTS (SELECT 1 FROM public."OcupacionesRuta" WHERE "LiberadaEnUtc" IS NULL AND "AnuladaEnUtc" IS NULL
                        GROUP BY "OperadorId", "VehiculoId" HAVING count(*) > 1)
                        OR EXISTS (SELECT 1 FROM public."OcupacionesRuta" WHERE "LiberadaEnUtc" IS NULL AND "AnuladaEnUtc" IS NULL
                        GROUP BY "OperadorId", "RepartidorAccesoId" HAVING count(*) > 1) THEN
                        RAISE EXCEPTION 'Fleet occupation conflicts detected. Keep StageFleetOccupations, inspect the fleet panel and resolve every conflicting authorization explicitly before activating exclusivity.';
                    END IF;
                    IF EXISTS (SELECT 1 FROM public."OcupacionesRuta" WHERE "Legada" AND "AdquiridaEnUtc" IS NULL
                        AND "LiberadaEnUtc" IS NULL AND "AnuladaEnUtc" IS NULL) THEN
                        RAISE EXCEPTION 'Legacy fleet occupations require explicit current verification or resolution before activating exclusivity; no historical facts were inferred.';
                    END IF;
                END $activation$;
                DROP POLICY migration_fleet_activation_read ON public."OcupacionesRuta";
                """);
            migrationBuilder.DropIndex(
                name: "IX_OcupacionesRuta_OperadorId_VehiculoId",
                table: "OcupacionesRuta");

            migrationBuilder.CreateIndex(
                name: "IX_OcupacionesRuta_OperadorId_RepartidorAccesoId",
                table: "OcupacionesRuta",
                columns: new[] { "OperadorId", "RepartidorAccesoId" },
                unique: true,
                filter: "\"LiberadaEnUtc\" IS NULL AND \"AnuladaEnUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OcupacionesRuta_OperadorId_VehiculoId",
                table: "OcupacionesRuta",
                columns: new[] { "OperadorId", "VehiculoId" },
                unique: true,
                filter: "\"LiberadaEnUtc\" IS NULL AND \"AnuladaEnUtc\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OcupacionesRuta_OperadorId_RepartidorAccesoId",
                table: "OcupacionesRuta");

            migrationBuilder.DropIndex(
                name: "IX_OcupacionesRuta_OperadorId_VehiculoId",
                table: "OcupacionesRuta");

            migrationBuilder.CreateIndex(
                name: "IX_OcupacionesRuta_OperadorId_VehiculoId",
                table: "OcupacionesRuta",
                columns: new[] { "OperadorId", "VehiculoId" });
        }
    }
}
