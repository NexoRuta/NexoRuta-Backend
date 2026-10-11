using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvioId = table.Column<Guid>(type: "uuid", nullable: false),
                    Tipo = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Destino = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Estado = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    Intentos = table.Column<int>(type: "integer", nullable: false),
                    CreadoEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProximoIntentoEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublicadoEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UltimoError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OutboxMessages_Envios_OperadorId_EnvioId",
                        columns: x => new { x.OperadorId, x.EnvioId },
                        principalTable: "Envios",
                        principalColumns: new[] { "OperadorId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Estado_ProximoIntentoEnUtc",
                table: "OutboxMessages",
                columns: new[] { "Estado", "ProximoIntentoEnUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_OperadorId_EnvioId",
                table: "OutboxMessages",
                columns: new[] { "OperadorId", "EnvioId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OutboxMessages");
        }
    }
}
