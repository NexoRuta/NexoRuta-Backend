using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRoutesCustodyAndDestinations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EntregaFisicaEnUtc",
                table: "Envios",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "Envios",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ConfirmadaEnUtc",
                table: "Direcciones",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ConfirmadaPorUsuarioId",
                table: "Direcciones",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Latitud",
                table: "Direcciones",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Longitud",
                table: "Direcciones",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "Direcciones",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "AccesoCustodioId",
                table: "Bultos",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Custodia",
                table: "Bultos",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "SinRecepcion");

            migrationBuilder.AddColumn<Guid>(
                name: "RutaCustodiaId",
                table: "Bultos",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "Bultos",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Repartidores_AccesoId_OperadorId",
                table: "Repartidores",
                columns: new[] { "AccesoId", "OperadorId" });

            migrationBuilder.CreateTable(
                name: "HojasRuta",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    RepartidorAccesoId = table.Column<Guid>(type: "uuid", nullable: false),
                    VehiculoId = table.Column<Guid>(type: "uuid", nullable: false),
                    VehiculoMatricula = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    VehiculoPesoMaximoGramos = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    VehiculoVolumenMaximoCm3 = table.Column<decimal>(type: "numeric(24,2)", precision: 24, scale: 2, nullable: false),
                    JornadaId = table.Column<Guid>(type: "uuid", nullable: false),
                    Fecha = table.Column<DateOnly>(type: "date", nullable: false),
                    ConfiguracionVersion = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    Estado = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    InicioEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UltimaSecuencia = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HojasRuta", x => x.Id);
                    table.UniqueConstraint("AK_HojasRuta_OperadorId_Id", x => new { x.OperadorId, x.Id });
                    table.CheckConstraint("CK_HojasRuta_Secuencia", "\"UltimaSecuencia\" >= 0");
                    table.ForeignKey(
                        name: "FK_HojasRuta_ConfiguracionesReparto_OperadorId_ConfiguracionVe~",
                        columns: x => new { x.OperadorId, x.ConfiguracionVersion },
                        principalTable: "ConfiguracionesReparto",
                        principalColumns: new[] { "OperadorId", "Version" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HojasRuta_Repartidores_RepartidorAccesoId_OperadorId",
                        columns: x => new { x.RepartidorAccesoId, x.OperadorId },
                        principalTable: "Repartidores",
                        principalColumns: new[] { "AccesoId", "OperadorId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HojasRuta_Vehiculos_OperadorId_VehiculoId",
                        columns: x => new { x.OperadorId, x.VehiculoId },
                        principalTable: "Vehiculos",
                        principalColumns: new[] { "OperadorId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ParadasRuta",
                columns: table => new
                {
                    RutaId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvioId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Orden = table.Column<int>(type: "integer", nullable: false),
                    LlegadaEstimadaUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Activa = table.Column<bool>(type: "boolean", nullable: false),
                    MotivoSuspension = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ParadasRuta", x => new { x.RutaId, x.EnvioId });
                    table.CheckConstraint("CK_ParadasRuta_Orden", "\"Orden\" > 0");
                    table.ForeignKey(
                        name: "FK_ParadasRuta_Envios_OperadorId_EnvioId",
                        columns: x => new { x.OperadorId, x.EnvioId },
                        principalTable: "Envios",
                        principalColumns: new[] { "OperadorId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ParadasRuta_HojasRuta_OperadorId_RutaId",
                        columns: x => new { x.OperadorId, x.RutaId },
                        principalTable: "HojasRuta",
                        principalColumns: new[] { "OperadorId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Envios_Cancelacion",
                table: "Envios",
                sql: "\"Estado\" <> 'Cancelado' OR \"EntregaFisicaEnUtc\" IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Envios_EntregaFisica",
                table: "Envios",
                sql: "\"Estado\" NOT IN ('Entregado','EntregaPendienteValidacion','EntregaSinValidacion') OR \"EntregaFisicaEnUtc\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Envios_Version",
                table: "Envios",
                sql: "\"Version\" >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_Bultos_AccesoCustodioId_OperadorId",
                table: "Bultos",
                columns: new[] { "AccesoCustodioId", "OperadorId" });

            migrationBuilder.CreateIndex(
                name: "IX_Bultos_OperadorId_RutaCustodiaId",
                table: "Bultos",
                columns: new[] { "OperadorId", "RutaCustodiaId" });

            migrationBuilder.CreateIndex(
                name: "IX_HojasRuta_JornadaId",
                table: "HojasRuta",
                column: "JornadaId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HojasRuta_OperadorId_ConfiguracionVersion",
                table: "HojasRuta",
                columns: new[] { "OperadorId", "ConfiguracionVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_HojasRuta_OperadorId_RepartidorAccesoId_Fecha",
                table: "HojasRuta",
                columns: new[] { "OperadorId", "RepartidorAccesoId", "Fecha" },
                unique: true,
                filter: "\"Estado\" <> 'Finalizada'");

            migrationBuilder.CreateIndex(
                name: "IX_HojasRuta_OperadorId_VehiculoId_Fecha",
                table: "HojasRuta",
                columns: new[] { "OperadorId", "VehiculoId", "Fecha" },
                unique: true,
                filter: "\"Estado\" <> 'Finalizada'");

            migrationBuilder.CreateIndex(
                name: "IX_HojasRuta_RepartidorAccesoId_OperadorId",
                table: "HojasRuta",
                columns: new[] { "RepartidorAccesoId", "OperadorId" });

            migrationBuilder.CreateIndex(
                name: "IX_ParadasRuta_OperadorId_EnvioId",
                table: "ParadasRuta",
                columns: new[] { "OperadorId", "EnvioId" },
                unique: true,
                filter: "\"Activa\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_ParadasRuta_OperadorId_RutaId",
                table: "ParadasRuta",
                columns: new[] { "OperadorId", "RutaId" });

            migrationBuilder.CreateIndex(
                name: "IX_ParadasRuta_RutaId_Orden",
                table: "ParadasRuta",
                columns: new[] { "RutaId", "Orden" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Bultos_HojasRuta_OperadorId_RutaCustodiaId",
                table: "Bultos",
                columns: new[] { "OperadorId", "RutaCustodiaId" },
                principalTable: "HojasRuta",
                principalColumns: new[] { "OperadorId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Bultos_Repartidores_AccesoCustodioId_OperadorId",
                table: "Bultos",
                columns: new[] { "AccesoCustodioId", "OperadorId" },
                principalTable: "Repartidores",
                principalColumns: new[] { "AccesoId", "OperadorId" },
                onDelete: ReferentialAction.Restrict);
            foreach (var tabla in new[] { "HojasRuta", "ParadasRuta" })
                migrationBuilder.Sql($"""
                    ALTER TABLE "{tabla}" ENABLE ROW LEVEL SECURITY;
                    ALTER TABLE "{tabla}" FORCE ROW LEVEL SECURITY;
                    CREATE POLICY operador_reparto ON "{tabla}"
                    USING ("OperadorId" = NULLIF(current_setting('nexoruta.operador_id', true), '')::uuid)
                    WITH CHECK ("OperadorId" = NULLIF(current_setting('nexoruta.operador_id', true), '')::uuid);
                    """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Bultos_HojasRuta_OperadorId_RutaCustodiaId",
                table: "Bultos");

            migrationBuilder.DropForeignKey(
                name: "FK_Bultos_Repartidores_AccesoCustodioId_OperadorId",
                table: "Bultos");

            migrationBuilder.DropTable(
                name: "ParadasRuta");

            migrationBuilder.DropTable(
                name: "HojasRuta");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Repartidores_AccesoId_OperadorId",
                table: "Repartidores");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Envios_Cancelacion",
                table: "Envios");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Envios_EntregaFisica",
                table: "Envios");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Envios_Version",
                table: "Envios");

            migrationBuilder.DropIndex(
                name: "IX_Bultos_AccesoCustodioId_OperadorId",
                table: "Bultos");

            migrationBuilder.DropIndex(
                name: "IX_Bultos_OperadorId_RutaCustodiaId",
                table: "Bultos");

            migrationBuilder.DropColumn(
                name: "EntregaFisicaEnUtc",
                table: "Envios");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Envios");

            migrationBuilder.DropColumn(
                name: "ConfirmadaEnUtc",
                table: "Direcciones");

            migrationBuilder.DropColumn(
                name: "ConfirmadaPorUsuarioId",
                table: "Direcciones");

            migrationBuilder.DropColumn(
                name: "Latitud",
                table: "Direcciones");

            migrationBuilder.DropColumn(
                name: "Longitud",
                table: "Direcciones");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Direcciones");

            migrationBuilder.DropColumn(
                name: "AccesoCustodioId",
                table: "Bultos");

            migrationBuilder.DropColumn(
                name: "Custodia",
                table: "Bultos");

            migrationBuilder.DropColumn(
                name: "RutaCustodiaId",
                table: "Bultos");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Bultos");
        }
    }
}
