using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryPoliciesAndVehicles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConfiguracionesReparto",
                columns: table => new
                {
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    ZonaHoraria = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    MaximoParadas = table.Column<int>(type: "integer", nullable: false),
                    MaximoIntentos = table.Column<int>(type: "integer", nullable: false),
                    RequiereFirma = table.Column<bool>(type: "boolean", nullable: false),
                    RequiereFoto = table.Column<bool>(type: "boolean", nullable: false),
                    RequiereNombre = table.Column<bool>(type: "boolean", nullable: false),
                    RequiereDocumento = table.Column<bool>(type: "boolean", nullable: false),
                    RequiereFotoIntento = table.Column<bool>(type: "boolean", nullable: false),
                    BloquearDiscrepanciasCarga = table.Column<bool>(type: "boolean", nullable: false),
                    Motivos = table.Column<string[]>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfiguracionesReparto", x => new { x.OperadorId, x.Version });
                    table.CheckConstraint("CK_ConfiguracionesReparto_Limites", "\"MaximoParadas\" BETWEEN 1 AND 500 AND \"MaximoIntentos\" BETWEEN 1 AND 100");
                    table.CheckConstraint("CK_ConfiguracionesReparto_Motivos", "cardinality(\"Motivos\") BETWEEN 1 AND 100");
                    table.CheckConstraint("CK_ConfiguracionesReparto_Version", "\"Version\" > 0");
                    table.ForeignKey(
                        name: "FK_ConfiguracionesReparto_Operadores_OperadorId",
                        column: x => x.OperadorId,
                        principalTable: "Operadores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Vehiculos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Matricula = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PesoMaximoGramos = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    VolumenMaximoCm3 = table.Column<decimal>(type: "numeric(24,2)", precision: 24, scale: 2, nullable: false),
                    Activo = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vehiculos", x => x.Id);
                    table.UniqueConstraint("AK_Vehiculos_OperadorId_Id", x => new { x.OperadorId, x.Id });
                    table.CheckConstraint("CK_Vehiculos_Capacidad", "\"PesoMaximoGramos\" > 0 AND \"VolumenMaximoCm3\" > 0");
                    table.ForeignKey(
                        name: "FK_Vehiculos_Operadores_OperadorId",
                        column: x => x.OperadorId,
                        principalTable: "Operadores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Vehiculos_OperadorId_Matricula",
                table: "Vehiculos",
                columns: new[] { "OperadorId", "Matricula" },
                unique: true);

            foreach (var tabla in new[] { "ConfiguracionesReparto", "Vehiculos" })
                migrationBuilder.Sql($"""
                    ALTER TABLE "{tabla}" ENABLE ROW LEVEL SECURITY;
                    ALTER TABLE "{tabla}" FORCE ROW LEVEL SECURITY;
                    CREATE POLICY "{tabla}_Operador" ON "{tabla}"
                        USING ("OperadorId" = NULLIF(current_setting('nexoruta.operador_id', true), '')::uuid)
                        WITH CHECK ("OperadorId" = NULLIF(current_setting('nexoruta.operador_id', true), '')::uuid);
                    """);
            migrationBuilder.Sql("""
                CREATE FUNCTION "ImpedirCambioConfiguracionReparto"() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'La política es inmutable; crear una nueva versión.' USING ERRCODE='55000'; END;
                $$;
                CREATE TRIGGER "ConfiguracionRepartoInmutable" BEFORE UPDATE OR DELETE ON "ConfiguracionesReparto"
                    FOR EACH ROW EXECUTE FUNCTION "ImpedirCambioConfiguracionReparto"();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConfiguracionesReparto");

            migrationBuilder.Sql("DROP FUNCTION \"ImpedirCambioConfiguracionReparto\"();");

            migrationBuilder.DropTable(
                name: "Vehiculos");
        }
    }
}
