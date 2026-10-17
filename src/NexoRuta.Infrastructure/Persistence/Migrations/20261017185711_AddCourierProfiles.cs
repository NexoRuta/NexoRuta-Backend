using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCourierProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_AccesosUsuario_Id_TipoOrganizacion_OrganizacionId",
                table: "AccesosUsuario",
                columns: new[] { "Id", "TipoOrganizacion", "OrganizacionId" });

            migrationBuilder.CreateTable(
                name: "Repartidores",
                columns: table => new
                {
                    AccesoId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    TipoOrganizacion = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Nombre = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Activo = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Repartidores", x => x.AccesoId);
                    table.CheckConstraint("CK_Repartidores_Nombre", "length(btrim(\"Nombre\")) > 0");
                    table.CheckConstraint("CK_Repartidores_Operador", "\"TipoOrganizacion\" = 'Operador'");
                    table.CheckConstraint("CK_Repartidores_Version", "\"Version\" >= 0");
                    table.ForeignKey(
                        name: "FK_Repartidores_AccesosUsuario_AccesoId_TipoOrganizacion_Opera~",
                        columns: x => new { x.AccesoId, x.TipoOrganizacion, x.OperadorId },
                        principalTable: "AccesosUsuario",
                        principalColumns: new[] { "Id", "TipoOrganizacion", "OrganizacionId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Repartidores_Operadores_OperadorId",
                        column: x => x.OperadorId,
                        principalTable: "Operadores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Repartidores_AccesoId_TipoOrganizacion_OperadorId",
                table: "Repartidores",
                columns: new[] { "AccesoId", "TipoOrganizacion", "OperadorId" });

            migrationBuilder.CreateIndex(
                name: "IX_Repartidores_OperadorId_Activo",
                table: "Repartidores",
                columns: new[] { "OperadorId", "Activo" });

            migrationBuilder.InsertData("Permisos", new[] { "Id", "Codigo", "Nombre" },
                new object[] { new Guid("019e0000-0000-7000-8000-000000000101"), "reparto.ejecutar", "Ejecutar la hoja de ruta propia" });
            migrationBuilder.Sql("""
                ALTER TABLE "Repartidores" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE "Repartidores" FORCE ROW LEVEL SECURITY;
                CREATE POLICY "Repartidores_Operador" ON "Repartidores"
                    USING ("OperadorId" = NULLIF(current_setting('nexoruta.operador_id', true), '')::uuid)
                    WITH CHECK ("OperadorId" = NULLIF(current_setting('nexoruta.operador_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM "RolesPermisos" WHERE "PermisoId" = '019e0000-0000-7000-8000-000000000101';
                DELETE FROM "Permisos" WHERE "Id" = '019e0000-0000-7000-8000-000000000101';
                """);
            migrationBuilder.DropTable(
                name: "Repartidores");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_AccesosUsuario_Id_TipoOrganizacion_OrganizacionId",
                table: "AccesosUsuario");
        }
    }
}
