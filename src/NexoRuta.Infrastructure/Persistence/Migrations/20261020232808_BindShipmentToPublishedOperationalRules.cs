using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BindShipmentToPublishedOperationalRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                -- EF runs this migration in one transaction. The owner must see
                -- all rows for both the data guard and foreign-key validation;
                -- FORCE is restored before commit, including on failure rollback.
                ALTER TABLE public."Envios" NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."VersionesReglasOperativas" NO FORCE ROW LEVEL SECURITY;
                DO $guard$
                DECLARE was_forced boolean;
                BEGIN
                    SELECT relforcerowsecurity INTO was_forced FROM pg_class
                    WHERE oid = 'public."Envios"'::regclass;
                    IF was_forced THEN
                        ALTER TABLE public."Envios" NO FORCE ROW LEVEL SECURITY;
                    END IF;
                    IF EXISTS(SELECT 1 FROM public."Envios") THEN
                    RAISE EXCEPTION 'Development shipments have no historical operational rules. Preserve this database and initialize an empty development database before applying this contract.' USING ERRCODE='55000';
                    END IF;
                    IF was_forced THEN
                        ALTER TABLE public."Envios" FORCE ROW LEVEL SECURITY;
                    END IF;
                END $guard$;
                """);
            migrationBuilder.AddColumn<Guid>(
                name: "VersionReglasId",
                table: "Envios",
                type: "uuid",
                nullable: false);

            migrationBuilder.CreateIndex(
                name: "IX_Envios_OperadorId_VersionReglasId",
                table: "Envios",
                columns: new[] { "OperadorId", "VersionReglasId" });

            migrationBuilder.AddForeignKey(
                name: "FK_Envios_VersionesReglasOperativas_OperadorId_VersionReglasId",
                table: "Envios",
                columns: new[] { "OperadorId", "VersionReglasId" },
                principalTable: "VersionesReglasOperativas",
                principalColumns: new[] { "OperadorId", "Id" },
                onDelete: ReferentialAction.Restrict);
            ConfigurarAdmision(migrationBuilder);
            migrationBuilder.Sql("""
                ALTER TABLE public."Envios" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."VersionesReglasOperativas" FORCE ROW LEVEL SECURITY;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            RetirarAdmision(migrationBuilder);
            migrationBuilder.DropForeignKey(
                name: "FK_Envios_VersionesReglasOperativas_OperadorId_VersionReglasId",
                table: "Envios");

            migrationBuilder.DropIndex(
                name: "IX_Envios_OperadorId_VersionReglasId",
                table: "Envios");

            migrationBuilder.DropColumn(
                name: "VersionReglasId",
                table: "Envios");
        }
    }
}
