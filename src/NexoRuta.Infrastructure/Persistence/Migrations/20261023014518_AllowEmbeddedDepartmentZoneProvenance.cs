using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AllowEmbeddedDepartmentZoneProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ZonasCobertura_DepartamentosReferencia_CodigoDepartamento_V~",
                table: "ZonasCobertura");

            migrationBuilder.DropIndex(
                name: "IX_ZonasCobertura_CodigoDepartamento_VersionFuente",
                table: "ZonasCobertura");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ZonasCobertura_CodigoDepartamento_VersionFuente",
                table: "ZonasCobertura",
                columns: new[] { "CodigoDepartamento", "VersionFuente" });

            migrationBuilder.AddForeignKey(
                name: "FK_ZonasCobertura_DepartamentosReferencia_CodigoDepartamento_V~",
                table: "ZonasCobertura",
                columns: new[] { "CodigoDepartamento", "VersionFuente" },
                principalTable: "DepartamentosReferencia",
                principalColumns: new[] { "Codigo", "VersionFuente" },
                onDelete: ReferentialAction.Restrict);
        }
    }
}
