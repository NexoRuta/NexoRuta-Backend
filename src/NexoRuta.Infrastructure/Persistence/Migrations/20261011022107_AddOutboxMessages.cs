using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations;

public partial class AddOutboxMessages : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "OutboxMessages",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OperadorId = table.Column<Guid>(type: "uuid", nullable: true),
                EnvioId = table.Column<Guid>(type: "uuid", nullable: true),
                Ambito = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ClaveAmbito = table.Column<Guid>(type: "uuid", nullable: false),
                Tipo = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                VersionContrato = table.Column<int>(type: "integer", nullable: false),
                Destino = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Prioridad = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                Estado = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Payload = table.Column<string>(type: "jsonb", nullable: false),
                EstadoEntrega = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                Intentos = table.Column<int>(type: "integer", nullable: false),
                CreadoEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ProximoIntentoEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ProcesadorId = table.Column<Guid>(type: "uuid", nullable: true),
                ReservaHastaUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                PublicadoEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                UltimoError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                table.CheckConstraint("CK_OutboxMessages_Scope",
                    "(\"Ambito\" = 'Envio' AND \"OperadorId\" IS NOT NULL AND \"EnvioId\" IS NOT NULL AND \"ClaveAmbito\" = \"EnvioId\") OR " +
                    "(\"Ambito\" = 'Identidad' AND \"OperadorId\" IS NULL AND \"EnvioId\" IS NULL)");
                table.CheckConstraint("CK_OutboxMessages_Contract",
                    "(\"Tipo\" = 'GenerarSeguimiento' AND \"VersionContrato\" = 1 AND \"Destino\" = 'RabbitMq' AND \"Ambito\" = 'Envio' AND \"EstadoEntrega\" IS NULL) OR " +
                    "(\"Tipo\" = 'EnvioCreado' AND \"VersionContrato\" = 1 AND \"Destino\" = 'Kafka' AND \"Ambito\" = 'Envio' AND \"EstadoEntrega\" IS NULL) OR " +
                    "(\"Tipo\" = 'PasswordRecoveryEmail' AND \"VersionContrato\" = 1 AND \"Destino\" = 'RabbitMq' AND \"Ambito\" = 'Identidad' AND \"EstadoEntrega\" IN ('Pending', 'Delivered', 'Expired', 'Failed')) OR " +
                    "(\"Tipo\" = 'PasswordRecoveryRequested' AND \"VersionContrato\" = 1 AND \"Destino\" = 'Kafka' AND \"Ambito\" = 'Identidad' AND \"EstadoEntrega\" IS NULL)");
                table.CheckConstraint("CK_OutboxMessages_Priority", "\"Prioridad\" IN ('Baja', 'Normal', 'Alta')");
                table.CheckConstraint("CK_OutboxMessages_State", "\"Estado\" IN ('Pendiente', 'Procesando', 'Publicado', 'Error')");
                table.ForeignKey(
                    name: "FK_OutboxMessages_Envios_OperadorId_EnvioId",
                    columns: x => new { x.OperadorId, x.EnvioId },
                    principalTable: "Envios",
                    principalColumns: new[] { "OperadorId", "Id" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessages_Destino_Estado_ProximoIntentoEnUtc_Prioridad_CreadoEnUtc",
            table: "OutboxMessages",
            columns: new[] { "Destino", "Estado", "ProximoIntentoEnUtc", "Prioridad", "CreadoEnUtc" });
        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessages_Ambito_ClaveAmbito_Destino_Estado_CreadoEnUtc",
            table: "OutboxMessages",
            columns: new[] { "Ambito", "ClaveAmbito", "Destino", "Estado", "CreadoEnUtc" });
        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessages_OperadorId_EnvioId",
            table: "OutboxMessages",
            columns: new[] { "OperadorId", "EnvioId" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable(name: "OutboxMessages");
}
