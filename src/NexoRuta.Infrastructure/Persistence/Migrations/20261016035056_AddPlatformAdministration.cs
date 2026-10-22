using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformAdministration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Preserve legacy email values while enforcing the application's normalized uniqueness.
            // Existing normalized duplicates deliberately fail the migration; never merge identities.
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX "IX_Usuarios_EmailNormalizado"
                ON "Usuarios" (lower(btrim("Email")));
                """);

            migrationBuilder.CreateTable(
                name: "RolesPlataforma",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EsProtegidoSistema = table.Column<bool>(type: "boolean", nullable: false),
                    PuedeGestionarOrganizaciones = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RolesPlataforma", x => x.Id);
                    table.CheckConstraint("CK_RolesPlataforma_Protegido", "\"EsProtegidoSistema\"");
                });

            migrationBuilder.CreateTable(
                name: "AccesosPlataforma",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    AccesoId = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    RolId = table.Column<Guid>(type: "uuid", nullable: false),
                    Activo = table.Column<bool>(type: "boolean", nullable: false),
                    VersionSesion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccesosPlataforma", x => x.Id);
                    table.CheckConstraint("CK_AccesosPlataforma_Inicial", "\"Id\" = 1");
                    table.ForeignKey(
                        name: "FK_AccesosPlataforma_RolesPlataforma_RolId",
                        column: x => x.RolId,
                        principalTable: "RolesPlataforma",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccesosPlataforma_Usuarios_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccesosPlataforma_AccesoId",
                table: "AccesosPlataforma",
                column: "AccesoId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccesosPlataforma_RolId",
                table: "AccesosPlataforma",
                column: "RolId");

            migrationBuilder.CreateIndex(
                name: "IX_AccesosPlataforma_UsuarioId",
                table: "AccesosPlataforma",
                column: "UsuarioId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX \"IX_Usuarios_EmailNormalizado\";");

            migrationBuilder.DropTable(
                name: "AccesosPlataforma");

            migrationBuilder.DropTable(
                name: "RolesPlataforma");
        }
    }
}
