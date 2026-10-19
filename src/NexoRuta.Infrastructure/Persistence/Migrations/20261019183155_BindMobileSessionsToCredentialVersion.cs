using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BindMobileSessionsToCredentialVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "VersionCredencial",
                table: "SesionesMoviles",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddCheckConstraint(
                name: "CK_SesionesMoviles_VersionCredencial",
                table: "SesionesMoviles",
                sql: "\"VersionCredencial\" >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_SesionesMoviles_VersionCredencial",
                table: "SesionesMoviles");

            migrationBuilder.DropColumn(
                name: "VersionCredencial",
                table: "SesionesMoviles");
        }
    }
}
