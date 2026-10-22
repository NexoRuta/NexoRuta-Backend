using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationReactivationRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SolicitudesReactivacionOrganizacion",
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
                    table.PrimaryKey("PK_SolicitudesReactivacionOrganizacion", x => x.Id);
                    table.CheckConstraint("CK_SolicitudesReactivacionOrganizacion_Organizacion", "(\"OperadorId\" IS NULL) <> (\"ComercioId\" IS NULL)");
                    table.CheckConstraint("CK_SolicitudesReactivacionOrganizacion_Resolucion", "((\"Estado\" = 'Pendiente' AND \"ResueltaPorUsuarioId\" IS NULL AND \"ResueltaEnUtc\" IS NULL AND \"MotivoResolucion\" IS NULL) OR (\"Estado\" IN ('Aprobada', 'Rechazada', 'AtendidaPorSoporte') AND \"ResueltaPorUsuarioId\" IS NOT NULL AND \"ResueltaEnUtc\" IS NOT NULL AND \"MotivoResolucion\" IS NOT NULL))");
                    table.ForeignKey(
                        name: "FK_SolicitudesReactivacionOrganizacion_Comercios_ComercioId",
                        column: x => x.ComercioId,
                        principalTable: "Comercios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SolicitudesReactivacionOrganizacion_Operadores_OperadorId",
                        column: x => x.OperadorId,
                        principalTable: "Operadores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SolicitudesReactivacionOrganizacion_Usuarios_ResueltaPorUsu~",
                        column: x => x.ResueltaPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SolicitudesReactivacionOrganizacion_Usuarios_SolicitadaPorU~",
                        column: x => x.SolicitadaPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SolicitudesReactivacionOrganizacion_ComercioId",
                table: "SolicitudesReactivacionOrganizacion",
                column: "ComercioId");

            migrationBuilder.CreateIndex(
                name: "IX_SolicitudesReactivacionOrganizacion_OperadorId",
                table: "SolicitudesReactivacionOrganizacion",
                column: "OperadorId");

            migrationBuilder.CreateIndex(
                name: "IX_SolicitudesReactivacionOrganizacion_ResueltaPorUsuarioId",
                table: "SolicitudesReactivacionOrganizacion",
                column: "ResueltaPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_SolicitudesReactivacionOrganizacion_SolicitadaPorUsuarioId",
                table: "SolicitudesReactivacionOrganizacion",
                column: "SolicitadaPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "UX_SolicitudesReactivacion_Pendiente",
                table: "SolicitudesReactivacionOrganizacion",
                columns: new[] { "TipoOrganizacion", "OrganizacionId" },
                unique: true,
                filter: "\"Estado\" = 'Pendiente'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SolicitudesReactivacionOrganizacion");
        }
    }
}
