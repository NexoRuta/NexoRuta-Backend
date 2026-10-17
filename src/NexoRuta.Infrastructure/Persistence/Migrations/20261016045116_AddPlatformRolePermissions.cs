using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformRolePermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RolesPlataformaPermisos",
                columns: table => new
                {
                    RolId = table.Column<Guid>(type: "uuid", nullable: false),
                    PermisoId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolesPlataformaPermisos", x => new { x.RolId, x.PermisoId });
                    table.ForeignKey(
                        name: "FK_RolesPlataformaPermisos_Permisos_PermisoId",
                        column: x => x.PermisoId,
                        principalTable: "Permisos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RolesPlataformaPermisos_RolesPlataforma_RolId",
                        column: x => x.RolId,
                        principalTable: "RolesPlataforma",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RolesPlataformaPermisos_PermisoId",
                table: "RolesPlataformaPermisos",
                column: "PermisoId");
            migrationBuilder.Sql("""
                INSERT INTO "Permisos" ("Id", "Codigo", "Nombre")
                SELECT gen_random_uuid(), 'plataforma.organizaciones.gestionar', 'Manage platform organizations'
                WHERE EXISTS (SELECT 1 FROM "RolesPlataforma" WHERE "PuedeGestionarOrganizaciones")
                ON CONFLICT ("Codigo") DO NOTHING;
                INSERT INTO "RolesPlataformaPermisos" ("RolId", "PermisoId")
                SELECT r."Id", p."Id" FROM "RolesPlataforma" r CROSS JOIN "Permisos" p
                WHERE r."PuedeGestionarOrganizaciones" AND p."Codigo" = 'plataforma.organizaciones.gestionar';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RolesPlataformaPermisos");
        }
    }
}
