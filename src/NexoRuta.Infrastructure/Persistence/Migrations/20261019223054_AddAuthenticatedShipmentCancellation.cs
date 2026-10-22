using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAuthenticatedShipmentCancellation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CancelacionesEnvio",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvioId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccesoId = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    TipoActor = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    OrganizacionActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Motivo = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    EstadoAnterior = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    VersionAnterior = table.Column<long>(type: "bigint", nullable: false),
                    VersionEnvio = table.Column<long>(type: "bigint", nullable: false),
                    CreadaEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CancelacionesEnvio", x => x.Id);
                    table.CheckConstraint("CK_CancelacionesEnvio_Version", "\"VersionAnterior\" >= 0 AND \"VersionEnvio\" = \"VersionAnterior\" + 1");
                    table.ForeignKey(
                        name: "FK_CancelacionesEnvio_AccesosUsuario_AccesoId_TipoActor_Organi~",
                        columns: x => new { x.AccesoId, x.TipoActor, x.OrganizacionActorId },
                        principalTable: "AccesosUsuario",
                        principalColumns: new[] { "Id", "TipoOrganizacion", "OrganizacionId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CancelacionesEnvio_Envios_OperadorId_EnvioId",
                        columns: x => new { x.OperadorId, x.EnvioId },
                        principalTable: "Envios",
                        principalColumns: new[] { "OperadorId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CancelacionesEnvio_Usuarios_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CancelacionesEnvio_AccesoId_TipoActor_OrganizacionActorId",
                table: "CancelacionesEnvio",
                columns: new[] { "AccesoId", "TipoActor", "OrganizacionActorId" });

            migrationBuilder.CreateIndex(
                name: "IX_CancelacionesEnvio_OperadorId_EnvioId",
                table: "CancelacionesEnvio",
                columns: new[] { "OperadorId", "EnvioId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CancelacionesEnvio_UsuarioId",
                table: "CancelacionesEnvio",
                column: "UsuarioId");
            ConfigureCancellation(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            RemoveCancellation(migrationBuilder);
            migrationBuilder.DropTable(
                name: "CancelacionesEnvio");
        }
    }
}
