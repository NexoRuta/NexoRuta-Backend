using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ShipmentCommerce : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Envios_OperadoresComercios_OperadorId_OperadorComercioId",
                table: "Envios");

            migrationBuilder.DropIndex(
                name: "IX_Envios_OperadorId_OperadorComercioId",
                table: "Envios");

            migrationBuilder.RenameColumn(
                name: "OperadorComercioId",
                table: "Envios",
                newName: "ComercioId");

            migrationBuilder.Sql("""
                UPDATE "Envios" e
                SET "ComercioId" = r."ComercioId"
                FROM "OperadoresComercios" r
                WHERE e."ComercioId" = r."Id";
                """);

            migrationBuilder.DropTable(
                name: "OperadoresComercios");

            migrationBuilder.CreateIndex(
                name: "IX_Envios_ComercioId",
                table: "Envios",
                column: "ComercioId");

            migrationBuilder.AddForeignKey(
                name: "FK_Envios_Comercios_ComercioId",
                table: "Envios",
                column: "ComercioId",
                principalTable: "Comercios",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Envios_Comercios_ComercioId",
                table: "Envios");

            migrationBuilder.DropIndex(
                name: "IX_Envios_ComercioId",
                table: "Envios");

            migrationBuilder.RenameColumn(
                name: "ComercioId",
                table: "Envios",
                newName: "OperadorComercioId");

            migrationBuilder.CreateTable(
                name: "OperadoresComercios",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ComercioId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperadoresComercios", x => x.Id);
                    table.UniqueConstraint("AK_OperadoresComercios_OperadorId_Id", x => new { x.OperadorId, x.Id });
                    table.ForeignKey(
                        name: "FK_OperadoresComercios_Comercios_ComercioId",
                        column: x => x.ComercioId,
                        principalTable: "Comercios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OperadoresComercios_Operadores_OperadorId",
                        column: x => x.OperadorId,
                        principalTable: "Operadores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // Al revertir se reconstruyen únicamente los vínculos con envíos.
            migrationBuilder.Sql("""
                INSERT INTO "OperadoresComercios" ("Id", "OperadorId", "ComercioId")
                SELECT gen_random_uuid(), pares."OperadorId", pares."ComercioId"
                FROM (
                    SELECT DISTINCT "OperadorId", "OperadorComercioId" AS "ComercioId"
                    FROM "Envios"
                ) pares;

                UPDATE "Envios" e
                SET "OperadorComercioId" = r."Id"
                FROM "OperadoresComercios" r
                WHERE e."OperadorId" = r."OperadorId"
                    AND e."OperadorComercioId" = r."ComercioId";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Envios_OperadorId_OperadorComercioId",
                table: "Envios",
                columns: new[] { "OperadorId", "OperadorComercioId" });

            migrationBuilder.CreateIndex(
                name: "IX_OperadoresComercios_ComercioId",
                table: "OperadoresComercios",
                column: "ComercioId");

            migrationBuilder.CreateIndex(
                name: "IX_OperadoresComercios_OperadorId_ComercioId",
                table: "OperadoresComercios",
                columns: new[] { "OperadorId", "ComercioId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Envios_OperadoresComercios_OperadorId_OperadorComercioId",
                table: "Envios",
                columns: new[] { "OperadorId", "OperadorComercioId" },
                principalTable: "OperadoresComercios",
                principalColumns: new[] { "OperadorId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }
    }
}
