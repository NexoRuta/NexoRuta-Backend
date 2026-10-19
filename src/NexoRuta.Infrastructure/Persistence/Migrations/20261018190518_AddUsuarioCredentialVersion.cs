using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUsuarioCredentialVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "VersionCredencial",
                table: "Usuarios",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VersionCredencial",
                table: "Usuarios");
        }
    }
}
