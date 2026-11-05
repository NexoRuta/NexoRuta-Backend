using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ParcelResultsAndHomePickups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_TarifasZonaModalidad_Modality",
                table: "TarifasZonaModalidad");

            migrationBuilder.DropPrimaryKey(
                name: "PK_PruebasEntrega",
                table: "PruebasEntrega");

            migrationBuilder.AddColumn<bool>(
                name: "PermiteEntregaParcial",
                table: "VersionesReglasOperativas",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PoliticaPendientesParcial",
                table: "VersionesReglasOperativas",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "NuevaVisitaSinRecargo");

            migrationBuilder.AddColumn<decimal>(
                name: "RecargoNuevaVisitaUyu",
                table: "VersionesReglasOperativas",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "BultoId",
                table: "PruebasEntrega",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<bool>(
                name: "EsRetiro",
                table: "Envios",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FranjaRetiroDesdeUtc",
                table: "Envios",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FranjaRetiroHastaUtc",
                table: "Envios",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MotivoRetiro",
                table: "Envios",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SolicitudRetiroHuella",
                table: "Envios",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SolicitudRetiroId",
                table: "Envios",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EntregadoEnUtc",
                table: "Bultos",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MotivoNoEntrega",
                table: "Bultos",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PruebaVenceEnUtc",
                table: "Bultos",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Resultado",
                table: "Bultos",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Pendiente");

            migrationBuilder.AddPrimaryKey(
                name: "PK_PruebasEntrega",
                table: "PruebasEntrega",
                columns: new[] { "OperadorId", "EnvioId", "BultoId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_TarifasZonaModalidad_Modality",
                table: "TarifasZonaModalidad",
                sql: "\"Modalidad\" IN ('Estandar', 'Urgente', 'Retiro')");

            migrationBuilder.CreateIndex(
                name: "IX_Envios_ComercioId_SolicitudRetiroId",
                table: "Envios",
                columns: new[] { "ComercioId", "SolicitudRetiroId" },
                unique: true,
                filter: "\"SolicitudRetiroId\" IS NOT NULL");
            InstalarCircuitos(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_TarifasZonaModalidad_Modality",
                table: "TarifasZonaModalidad");

            migrationBuilder.DropPrimaryKey(
                name: "PK_PruebasEntrega",
                table: "PruebasEntrega");

            migrationBuilder.DropIndex(
                name: "IX_Envios_ComercioId_SolicitudRetiroId",
                table: "Envios");

            migrationBuilder.DropColumn(
                name: "PermiteEntregaParcial",
                table: "VersionesReglasOperativas");

            migrationBuilder.DropColumn(
                name: "PoliticaPendientesParcial",
                table: "VersionesReglasOperativas");

            migrationBuilder.DropColumn(
                name: "RecargoNuevaVisitaUyu",
                table: "VersionesReglasOperativas");

            migrationBuilder.DropColumn(
                name: "BultoId",
                table: "PruebasEntrega");

            migrationBuilder.DropColumn(
                name: "EsRetiro",
                table: "Envios");

            migrationBuilder.DropColumn(
                name: "FranjaRetiroDesdeUtc",
                table: "Envios");

            migrationBuilder.DropColumn(
                name: "FranjaRetiroHastaUtc",
                table: "Envios");

            migrationBuilder.DropColumn(
                name: "MotivoRetiro",
                table: "Envios");

            migrationBuilder.DropColumn(
                name: "SolicitudRetiroHuella",
                table: "Envios");

            migrationBuilder.DropColumn(
                name: "SolicitudRetiroId",
                table: "Envios");

            migrationBuilder.DropColumn(
                name: "EntregadoEnUtc",
                table: "Bultos");

            migrationBuilder.DropColumn(
                name: "MotivoNoEntrega",
                table: "Bultos");

            migrationBuilder.DropColumn(
                name: "PruebaVenceEnUtc",
                table: "Bultos");

            migrationBuilder.DropColumn(
                name: "Resultado",
                table: "Bultos");

            migrationBuilder.AddPrimaryKey(
                name: "PK_PruebasEntrega",
                table: "PruebasEntrega",
                columns: new[] { "OperadorId", "EnvioId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_TarifasZonaModalidad_Modality",
                table: "TarifasZonaModalidad",
                sql: "\"Modalidad\" IN ('Estandar', 'Urgente')");
        }
    }
}
