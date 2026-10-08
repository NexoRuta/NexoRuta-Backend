using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AccountOwnerAndShipmentOperator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (
                        SELECT a."UsuarioId"
                        FROM "AccesosUsuario" a
                        LEFT JOIN "OperadoresComercios" r ON r."Id" = a."OperadorComercioId"
                        GROUP BY a."UsuarioId"
                        HAVING COUNT(DISTINCT r."ComercioId") > 1
                            OR (COUNT(*) FILTER (WHERE a."OperadorComercioId" IS NULL) > 0
                                AND COUNT(*) FILTER (WHERE a."OperadorComercioId" IS NOT NULL) > 0)
                            OR COUNT(DISTINCT a."OperadorId") FILTER (WHERE a."OperadorComercioId" IS NULL) > 1
                    ) THEN
                        RAISE EXCEPTION 'Una cuenta tiene organizaciones incompatibles; revisar sus vínculos antes de migrar.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_AccesosUsuario_OperadoresComercios_OperadorId_OperadorComer~",
                table: "AccesosUsuario");

            migrationBuilder.DropIndex(
                name: "IX_AccesosUsuario_OperadorId_OperadorComercioId",
                table: "AccesosUsuario");

            migrationBuilder.DropIndex(
                name: "IX_AccesosUsuario_UsuarioId_OperadorComercioId",
                table: "AccesosUsuario");

            migrationBuilder.DropIndex(
                name: "IX_AccesosUsuario_UsuarioId_OperadorId",
                table: "AccesosUsuario");

            migrationBuilder.RenameColumn(
                name: "OperadorComercioId",
                table: "AccesosUsuario",
                newName: "ComercioId");

            migrationBuilder.AlterColumn<Guid>(
                name: "OperadorId",
                table: "AccesosUsuario",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<bool>(
                name: "EsPropietario",
                table: "AccesosUsuario",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("""
                UPDATE "AccesosUsuario" a
                SET "ComercioId" = r."ComercioId", "OperadorId" = NULL
                FROM "OperadoresComercios" r
                WHERE a."ComercioId" = r."Id";

                DELETE FROM "AccesosUsuario" a USING "AccesosUsuario" anterior
                WHERE a."UsuarioId" = anterior."UsuarioId" AND a."Id" > anterior."Id";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_AccesosUsuario_ComercioId",
                table: "AccesosUsuario",
                column: "ComercioId");

            migrationBuilder.CreateIndex(
                name: "IX_AccesosUsuario_OperadorId",
                table: "AccesosUsuario",
                column: "OperadorId");

            migrationBuilder.CreateIndex(
                name: "IX_AccesosUsuario_UsuarioId",
                table: "AccesosUsuario",
                column: "UsuarioId",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_AccesosUsuario_Pertenencia",
                table: "AccesosUsuario",
                sql: "(\"OperadorId\" IS NULL) <> (\"ComercioId\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AccesosUsuario_Propietario",
                table: "AccesosUsuario",
                sql: "NOT \"EsPropietario\" OR \"ComercioId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_AccesosUsuario_Comercios_ComercioId",
                table: "AccesosUsuario",
                column: "ComercioId",
                principalTable: "Comercios",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (
                        SELECT 1 FROM "AccesosUsuario" a
                        WHERE a."ComercioId" IS NOT NULL AND NOT EXISTS (
                            SELECT 1 FROM "OperadoresComercios" r WHERE r."ComercioId" = a."ComercioId"
                        )
                    ) THEN
                        RAISE EXCEPTION 'No se puede revertir una cuenta de comercio sin un vínculo con un operador.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_AccesosUsuario_Comercios_ComercioId",
                table: "AccesosUsuario");

            migrationBuilder.DropIndex(
                name: "IX_AccesosUsuario_ComercioId",
                table: "AccesosUsuario");

            migrationBuilder.DropIndex(
                name: "IX_AccesosUsuario_OperadorId",
                table: "AccesosUsuario");

            migrationBuilder.DropIndex(
                name: "IX_AccesosUsuario_UsuarioId",
                table: "AccesosUsuario");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AccesosUsuario_Pertenencia",
                table: "AccesosUsuario");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AccesosUsuario_Propietario",
                table: "AccesosUsuario");

            migrationBuilder.DropColumn(
                name: "EsPropietario",
                table: "AccesosUsuario");

            migrationBuilder.RenameColumn(
                name: "ComercioId",
                table: "AccesosUsuario",
                newName: "OperadorComercioId");

            migrationBuilder.Sql("""
                WITH vinculos AS (
                    SELECT a."UsuarioId", r."OperadorId", r."Id" AS "VinculoId",
                        ROW_NUMBER() OVER (PARTITION BY a."Id" ORDER BY r."Id") AS numero
                    FROM "AccesosUsuario" a
                    JOIN "OperadoresComercios" r ON r."ComercioId" = a."OperadorComercioId"
                    WHERE a."OperadorId" IS NULL
                )
                INSERT INTO "AccesosUsuario" ("Id", "UsuarioId", "OperadorId", "OperadorComercioId")
                SELECT uuidv7(), "UsuarioId", "OperadorId", "VinculoId" FROM vinculos WHERE numero > 1;

                WITH primeros AS (
                    SELECT DISTINCT ON (a."Id") a."Id", r."OperadorId", r."Id" AS "VinculoId"
                    FROM "AccesosUsuario" a
                    JOIN "OperadoresComercios" r ON r."ComercioId" = a."OperadorComercioId"
                    WHERE a."OperadorId" IS NULL
                    ORDER BY a."Id", r."Id"
                )
                UPDATE "AccesosUsuario" a
                SET "OperadorId" = p."OperadorId", "OperadorComercioId" = p."VinculoId"
                FROM primeros p WHERE a."Id" = p."Id";
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "OperadorId",
                table: "AccesosUsuario",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccesosUsuario_OperadorId_OperadorComercioId",
                table: "AccesosUsuario",
                columns: new[] { "OperadorId", "OperadorComercioId" });

            migrationBuilder.CreateIndex(
                name: "IX_AccesosUsuario_UsuarioId_OperadorComercioId",
                table: "AccesosUsuario",
                columns: new[] { "UsuarioId", "OperadorComercioId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccesosUsuario_UsuarioId_OperadorId",
                table: "AccesosUsuario",
                columns: new[] { "UsuarioId", "OperadorId" },
                unique: true,
                filter: "\"OperadorComercioId\" IS NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_AccesosUsuario_OperadoresComercios_OperadorId_OperadorComer~",
                table: "AccesosUsuario",
                columns: new[] { "OperadorId", "OperadorComercioId" },
                principalTable: "OperadoresComercios",
                principalColumns: new[] { "OperadorId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }
    }
}
