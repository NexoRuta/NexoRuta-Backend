using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EstablishOrganizationOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    ambiguous_commerces integer;
                    unassigned_commerces integer;
                    unassigned_operators integer;
                BEGIN
                    SELECT COUNT(*) INTO ambiguous_commerces
                    FROM (
                        SELECT "ComercioId"
                        FROM "AccesosUsuario"
                        WHERE "ComercioId" IS NOT NULL AND "EsPropietario" IS TRUE
                        GROUP BY "ComercioId"
                        HAVING COUNT(*) > 1
                    ) ambiguous;

                    UPDATE "AccesosUsuario" access
                    SET "EsPropietario" = FALSE
                    WHERE access."EsPropietario" IS TRUE
                      AND access."ComercioId" IN (
                          SELECT "ComercioId"
                          FROM "AccesosUsuario"
                          WHERE "ComercioId" IS NOT NULL AND "EsPropietario" IS TRUE
                          GROUP BY "ComercioId"
                          HAVING COUNT(*) > 1
                      );
                    UPDATE "AccesosUsuario" SET "EsPropietario" = FALSE WHERE "EsPropietario" IS NULL;

                    ALTER TABLE "AccesosUsuario" DROP CONSTRAINT IF EXISTS "CK_AccesosUsuario_Propietario";

                    SELECT COUNT(*) INTO unassigned_commerces
                    FROM "Comercios" organization
                    WHERE NOT EXISTS (
                        SELECT 1 FROM "AccesosUsuario" access
                        WHERE access."ComercioId" = organization."Id" AND access."EsPropietario"
                    );
                    SELECT COUNT(*) INTO unassigned_operators
                    FROM "Operadores" organization
                    WHERE NOT EXISTS (
                        SELECT 1 FROM "AccesosUsuario" access
                        WHERE access."OperadorId" = organization."Id" AND access."EsPropietario"
                    );
                    RAISE NOTICE 'Ownership backfill left % ambiguous commerce markers unassigned; % commerces and % operators have no recorded owner and require manual review.',
                        ambiguous_commerces, unassigned_commerces, unassigned_operators;
                END $$;
                """);

            migrationBuilder.AlterColumn<bool>(
                name: "EsPropietario",
                table: "AccesosUsuario",
                type: "boolean",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccesosUsuario_TipoOrganizacion_OrganizacionId",
                table: "AccesosUsuario",
                columns: new[] { "TipoOrganizacion", "OrganizacionId" },
                unique: true,
                filter: "\"EsPropietario\" = TRUE");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (
                        SELECT 1 FROM "AccesosUsuario"
                        WHERE "EsPropietario" IS TRUE AND "OperadorId" IS NOT NULL
                    ) THEN
                        RAISE EXCEPTION 'Cannot downgrade organization ownership while an operator owner is recorded.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropIndex(
                name: "IX_AccesosUsuario_TipoOrganizacion_OrganizacionId",
                table: "AccesosUsuario");

            migrationBuilder.AlterColumn<bool>(
                name: "EsPropietario",
                table: "AccesosUsuario",
                type: "boolean",
                nullable: true,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldDefaultValue: false);

            migrationBuilder.AddCheckConstraint(
                name: "CK_AccesosUsuario_Propietario",
                table: "AccesosUsuario",
                sql: "NOT \"EsPropietario\" OR \"ComercioId\" IS NOT NULL");
        }
    }
}
