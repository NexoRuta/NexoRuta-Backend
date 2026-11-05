using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RegisterCommerceNoticeContract : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_OutboxMessages_Contract",
                table: "OutboxMessages");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OutboxMessages_Contract",
                table: "OutboxMessages",
                sql: "(\"Tipo\" = 'GenerarSeguimiento' AND \"VersionContrato\" = 1 AND \"Destino\" = 'RabbitMq' AND \"Ambito\" = 'Envio' AND \"EstadoEntrega\" IS NULL) OR (\"Tipo\" = 'EnvioCreado' AND \"VersionContrato\" = 1 AND \"Destino\" = 'Kafka' AND \"Ambito\" = 'Envio' AND \"EstadoEntrega\" IS NULL) OR (\"Tipo\" = 'RepartoModificado' AND \"VersionContrato\" = 1 AND \"Destino\" = 'PushFcm' AND \"Ambito\" = 'Envio' AND \"EstadoEntrega\" IS NULL) OR (\"Tipo\" = 'CodigoCancelacionEmail' AND \"VersionContrato\" = 1 AND \"Destino\" = 'RabbitMq' AND \"Ambito\" = 'Envio' AND \"EstadoEntrega\" IS NULL) OR (\"Tipo\" = 'PasswordRecoveryEmail' AND \"VersionContrato\" = 1 AND \"Destino\" = 'RabbitMq' AND \"Ambito\" = 'Identidad' AND \"EstadoEntrega\" IN ('Pending', 'Delivered', 'Expired', 'Failed')) OR (\"Tipo\" = 'PasswordRecoveryRequested' AND \"VersionContrato\" = 1 AND \"Destino\" = 'Kafka' AND \"Ambito\" = 'Identidad' AND \"EstadoEntrega\" IS NULL) OR (\"Tipo\" IN ('BultoActualizado','OperacionActualizada') AND \"VersionContrato\" = 2 AND \"Destino\" = 'Kafka' AND \"Ambito\" = 'Envio' AND \"EstadoEntrega\" IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_OutboxMessages_Contract",
                table: "OutboxMessages");

            migrationBuilder.AddCheckConstraint(
                name: "CK_OutboxMessages_Contract",
                table: "OutboxMessages",
                sql: "(\"Tipo\" = 'GenerarSeguimiento' AND \"VersionContrato\" = 1 AND \"Destino\" = 'RabbitMq' AND \"Ambito\" = 'Envio' AND \"EstadoEntrega\" IS NULL) OR (\"Tipo\" = 'EnvioCreado' AND \"VersionContrato\" = 1 AND \"Destino\" = 'Kafka' AND \"Ambito\" = 'Envio' AND \"EstadoEntrega\" IS NULL) OR (\"Tipo\" = 'RepartoModificado' AND \"VersionContrato\" = 1 AND \"Destino\" = 'PushFcm' AND \"Ambito\" = 'Envio' AND \"EstadoEntrega\" IS NULL) OR (\"Tipo\" = 'CodigoCancelacionEmail' AND \"VersionContrato\" = 1 AND \"Destino\" = 'RabbitMq' AND \"Ambito\" = 'Envio' AND \"EstadoEntrega\" IS NULL) OR (\"Tipo\" = 'PasswordRecoveryEmail' AND \"VersionContrato\" = 1 AND \"Destino\" = 'RabbitMq' AND \"Ambito\" = 'Identidad' AND \"EstadoEntrega\" IN ('Pending', 'Delivered', 'Expired', 'Failed')) OR (\"Tipo\" = 'PasswordRecoveryRequested' AND \"VersionContrato\" = 1 AND \"Destino\" = 'Kafka' AND \"Ambito\" = 'Identidad' AND \"EstadoEntrega\" IS NULL)");
        }
    }
}
