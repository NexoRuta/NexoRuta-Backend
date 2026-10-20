using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMobileRefreshSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_AccesosUsuario_Id_UsuarioId",
                table: "AccesosUsuario",
                columns: new[] { "Id", "UsuarioId" });

            migrationBuilder.CreateTable(
                name: "SesionesMoviles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccesoId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionSesion = table.Column<long>(type: "bigint", nullable: false),
                    CreadaEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiraEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevocadaEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SesionesMoviles", x => x.Id);
                    table.CheckConstraint("CK_SesionesMoviles_Version", "\"VersionSesion\" >= 0");
                    table.CheckConstraint("CK_SesionesMoviles_Vigencia", "\"ExpiraEnUtc\" > \"CreadaEnUtc\"");
                    table.ForeignKey(
                        name: "FK_SesionesMoviles_AccesosUsuario_AccesoId_UsuarioId",
                        columns: x => new { x.AccesoId, x.UsuarioId },
                        principalTable: "AccesosUsuario",
                        principalColumns: new[] { "Id", "UsuarioId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SesionesMoviles_Usuarios_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TokensRenovacionMoviles",
                columns: table => new
                {
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SesionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConsumidoEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TokensRenovacionMoviles", x => x.Hash);
                    table.CheckConstraint("CK_TokensRenovacionMoviles_Hash", "\"Hash\" ~ '^[0-9A-F]{64}$'");
                    table.ForeignKey(
                        name: "FK_TokensRenovacionMoviles_SesionesMoviles_SesionId",
                        column: x => x.SesionId,
                        principalTable: "SesionesMoviles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SesionesMoviles_AccesoId_UsuarioId",
                table: "SesionesMoviles",
                columns: new[] { "AccesoId", "UsuarioId" });

            migrationBuilder.CreateIndex(
                name: "IX_SesionesMoviles_ExpiraEnUtc",
                table: "SesionesMoviles",
                column: "ExpiraEnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SesionesMoviles_UsuarioId",
                table: "SesionesMoviles",
                column: "UsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_TokensRenovacionMoviles_SesionId",
                table: "TokensRenovacionMoviles",
                column: "SesionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TokensRenovacionMoviles");

            migrationBuilder.DropTable(
                name: "SesionesMoviles");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_AccesosUsuario_Id_UsuarioId",
                table: "AccesosUsuario");
        }
    }
}
