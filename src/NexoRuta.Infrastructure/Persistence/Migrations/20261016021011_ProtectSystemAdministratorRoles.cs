using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProtectSystemAdministratorRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EsProtegidoSistema",
                table: "Roles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // The configured initial operator role has this established code; other roles remain editable.
            migrationBuilder.Sql("UPDATE \"Roles\" SET \"EsProtegidoSistema\" = TRUE WHERE \"OperadorId\" IS NOT NULL AND \"Codigo\" = 'administrador_operador';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EsProtegidoSistema",
                table: "Roles");
        }
    }
}
