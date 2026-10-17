using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationSuspensionAndCancellation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Estado",
                table: "Operadores",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Activa");

            migrationBuilder.AddColumn<string>(
                name: "Estado",
                table: "Comercios",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Activa");

            migrationBuilder.CreateTable(
                name: "SolicitudesBajaOrganizacion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: true),
                    ComercioId = table.Column<Guid>(type: "uuid", nullable: true),
                    TipoOrganizacion = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, computedColumnSql: "CASE WHEN \"OperadorId\" IS NOT NULL THEN 'Operador' ELSE 'Comercio' END", stored: true),
                    OrganizacionId = table.Column<Guid>(type: "uuid", nullable: false, computedColumnSql: "COALESCE(\"OperadorId\", \"ComercioId\")", stored: true),
                    SolicitadaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    SolicitadaEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Motivo = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Estado = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Pendiente"),
                    ResueltaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResueltaEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    MotivoResolucion = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SolicitudesBajaOrganizacion", x => x.Id);
                    table.CheckConstraint("CK_SolicitudesBajaOrganizacion_Organizacion", "(\"OperadorId\" IS NULL) <> (\"ComercioId\" IS NULL)");
                    table.CheckConstraint("CK_SolicitudesBajaOrganizacion_Resolucion", "((\"Estado\" = 'Pendiente' AND \"ResueltaPorUsuarioId\" IS NULL AND \"ResueltaEnUtc\" IS NULL AND \"MotivoResolucion\" IS NULL) OR (\"Estado\" IN ('Aprobada', 'Rechazada') AND \"ResueltaPorUsuarioId\" IS NOT NULL AND \"ResueltaEnUtc\" IS NOT NULL AND \"MotivoResolucion\" IS NOT NULL))");
                    table.ForeignKey(
                        name: "FK_SolicitudesBajaOrganizacion_Comercios_ComercioId",
                        column: x => x.ComercioId,
                        principalTable: "Comercios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SolicitudesBajaOrganizacion_Operadores_OperadorId",
                        column: x => x.OperadorId,
                        principalTable: "Operadores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SolicitudesBajaOrganizacion_Usuarios_ResueltaPorUsuarioId",
                        column: x => x.ResueltaPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SolicitudesBajaOrganizacion_Usuarios_SolicitadaPorUsuarioId",
                        column: x => x.SolicitadaPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CambiosEstadoOrganizacion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: true),
                    ComercioId = table.Column<Guid>(type: "uuid", nullable: true),
                    TipoOrganizacion = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, computedColumnSql: "CASE WHEN \"OperadorId\" IS NOT NULL THEN 'Operador' ELSE 'Comercio' END", stored: true),
                    OrganizacionId = table.Column<Guid>(type: "uuid", nullable: false, computedColumnSql: "COALESCE(\"OperadorId\", \"ComercioId\")", stored: true),
                    EstadoAnterior = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    EstadoNuevo = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ActorUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    OcurridoEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Motivo = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    SolicitudBajaId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CambiosEstadoOrganizacion", x => x.Id);
                    table.CheckConstraint("CK_CambiosEstadoOrganizacion_Organizacion", "(\"OperadorId\" IS NULL) <> (\"ComercioId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_CambiosEstadoOrganizacion_Comercios_ComercioId",
                        column: x => x.ComercioId,
                        principalTable: "Comercios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CambiosEstadoOrganizacion_Operadores_OperadorId",
                        column: x => x.OperadorId,
                        principalTable: "Operadores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CambiosEstadoOrganizacion_SolicitudesBajaOrganizacion_Solic~",
                        column: x => x.SolicitudBajaId,
                        principalTable: "SolicitudesBajaOrganizacion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CambiosEstadoOrganizacion_Usuarios_ActorUsuarioId",
                        column: x => x.ActorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CambiosEstadoOrganizacion_ActorUsuarioId",
                table: "CambiosEstadoOrganizacion",
                column: "ActorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_CambiosEstadoOrganizacion_ComercioId",
                table: "CambiosEstadoOrganizacion",
                column: "ComercioId");

            migrationBuilder.CreateIndex(
                name: "IX_CambiosEstadoOrganizacion_OperadorId",
                table: "CambiosEstadoOrganizacion",
                column: "OperadorId");

            migrationBuilder.CreateIndex(
                name: "IX_CambiosEstadoOrganizacion_SolicitudBajaId",
                table: "CambiosEstadoOrganizacion",
                column: "SolicitudBajaId");

            migrationBuilder.CreateIndex(
                name: "IX_CambiosEstadoOrganizacion_TipoOrganizacion_OrganizacionId_O~",
                table: "CambiosEstadoOrganizacion",
                columns: new[] { "TipoOrganizacion", "OrganizacionId", "OcurridoEnUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SolicitudesBajaOrganizacion_ComercioId",
                table: "SolicitudesBajaOrganizacion",
                column: "ComercioId");

            migrationBuilder.CreateIndex(
                name: "IX_SolicitudesBajaOrganizacion_OperadorId",
                table: "SolicitudesBajaOrganizacion",
                column: "OperadorId");

            migrationBuilder.CreateIndex(
                name: "IX_SolicitudesBajaOrganizacion_ResueltaPorUsuarioId",
                table: "SolicitudesBajaOrganizacion",
                column: "ResueltaPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_SolicitudesBajaOrganizacion_SolicitadaPorUsuarioId",
                table: "SolicitudesBajaOrganizacion",
                column: "SolicitadaPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "UX_SolicitudesBaja_Pendiente",
                table: "SolicitudesBajaOrganizacion",
                columns: new[] { "TipoOrganizacion", "OrganizacionId" },
                unique: true,
                filter: "\"Estado\" = 'Pendiente'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CambiosEstadoOrganizacion");

            migrationBuilder.DropTable(
                name: "SolicitudesBajaOrganizacion");

            migrationBuilder.DropColumn(
                name: "Estado",
                table: "Operadores");

            migrationBuilder.DropColumn(
                name: "Estado",
                table: "Comercios");
        }
    }
}
