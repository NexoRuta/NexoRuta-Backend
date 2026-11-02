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
                DO $activation$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname=current_user AND (rolsuper OR rolbypassrls)) THEN
                        RAISE EXCEPTION 'Fleet activation requires an authorized migration identity able to inspect all tenants.';
                    END IF;
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
