using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExplicitVisitOutcome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ResultadoVisita",
                table: "ParadasRuta",
                type: "text",
                nullable: false,
                defaultValue: "Pendiente");
            migrationBuilder.Sql("""
                UPDATE public."ParadasRuta" p SET "ResultadoVisita"=CASE WHEN p."Activa" THEN 'Pendiente' WHEN p."MotivoSuspension" IS NOT NULL THEN 'Suspendida'
                WHEN EXISTS(SELECT 1 FROM public."Bultos" b JOIN public."Envios" e ON e."OperadorId"=b."OperadorId" AND e."Id"=b."EnvioId" WHERE b."OperadorId"=p."OperadorId" AND b."EnvioId"=p."EnvioId" AND b."RutaCustodiaId"=p."RutaId" AND b."Custodia"='Destinatario' AND e."EntregaFisicaEnUtc" IS NOT NULL) THEN 'Completada' ELSE 'CerradaSinResultado' END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ResultadoVisita",
                table: "ParadasRuta");
        }
    }
}
