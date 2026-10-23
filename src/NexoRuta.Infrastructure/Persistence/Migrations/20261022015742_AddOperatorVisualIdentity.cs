using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOperatorVisualIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VersionesIdentidadVisualOperador",
                columns: table => new
                {
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    NombreComercial = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    CorreoPublico = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    TelefonoPublico = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ColorPrimario = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    ColorSecundario = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    LogoContenido = table.Column<byte[]>(type: "bytea", nullable: true),
                    LogoTipoContenido = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    AutorUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VersionesIdentidadVisualOperador", x => new { x.OperadorId, x.Version });
                    table.CheckConstraint("CK_VersionesIdentidadVisualOperador_Colores", "(\"ColorPrimario\" IS NULL OR \"ColorPrimario\" ~ '^#[0-9A-F]{6}$') AND (\"ColorSecundario\" IS NULL OR \"ColorSecundario\" ~ '^#[0-9A-F]{6}$')");
                    table.CheckConstraint("CK_VersionesIdentidadVisualOperador_Logo", "(\"LogoContenido\" IS NULL AND \"LogoTipoContenido\" IS NULL) OR (\"LogoContenido\" IS NOT NULL AND \"LogoTipoContenido\" IS NOT NULL AND \"LogoTipoContenido\" IN ('image/png', 'image/jpeg') AND octet_length(\"LogoContenido\") BETWEEN 1 AND 1048576)");
                    table.CheckConstraint("CK_VersionesIdentidadVisualOperador_Version", "\"Version\" >= 1");
                    table.ForeignKey(
                        name: "FK_VersionesIdentidadVisualOperador_Operadores_OperadorId",
                        column: x => x.OperadorId,
                        principalTable: "Operadores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VersionesIdentidadVisualOperador_Usuarios_AutorUsuarioId",
                        column: x => x.AutorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VersionesIdentidadVisualOperador_AutorUsuarioId",
                table: "VersionesIdentidadVisualOperador",
                column: "AutorUsuarioId");
            ConfigurarIdentidadVisual(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            RetirarIdentidadVisual(migrationBuilder);
            migrationBuilder.DropTable(
                name: "VersionesIdentidadVisualOperador");
        }
    }
}
