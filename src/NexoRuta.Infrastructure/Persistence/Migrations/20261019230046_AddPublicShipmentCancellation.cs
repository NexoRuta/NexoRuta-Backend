using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPublicShipmentCancellation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_OutboxMessages_Contract",
                table: "OutboxMessages");

            migrationBuilder.AlterColumn<Guid>(
                name: "UsuarioId",
                table: "CancelacionesEnvio",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<string>(
                name: "TipoActor",
                table: "CancelacionesEnvio",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.AlterColumn<Guid>(
                name: "OrganizacionActorId",
                table: "CancelacionesEnvio",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "AccesoId",
                table: "CancelacionesEnvio",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "AutorizacionPublicaId",
                table: "CancelacionesEnvio",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CodigosCancelacionPublica",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvioId = table.Column<Guid>(type: "uuid", nullable: false),
                    MensajeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Hash = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreadoEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Activo = table.Column<bool>(type: "boolean", nullable: false),
                    Intentos = table.Column<int>(type: "integer", nullable: false),
                    ConsumidoEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CorreoEnviadoEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CodigosCancelacionPublica", x => x.Id);
                    table.UniqueConstraint("AK_CodigosCancelacionPublica_OperadorId_EnvioId_Id", x => new { x.OperadorId, x.EnvioId, x.Id });
                    table.CheckConstraint("CK_CodigosCancelacionPublica_Intentos", "\"Intentos\" BETWEEN 0 AND 5");
                    table.ForeignKey(
                        name: "FK_CodigosCancelacionPublica_Envios_OperadorId_EnvioId",
                        columns: x => new { x.OperadorId, x.EnvioId },
                        principalTable: "Envios",
                        principalColumns: new[] { "OperadorId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CodigosCancelacionPublica_OutboxMessages_MensajeId",
                        column: x => x.MensajeId,
                        principalTable: "OutboxMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_OutboxMessages_Contract",
                table: "OutboxMessages",
                sql: "(\"Tipo\" = 'GenerarSeguimiento' AND \"VersionContrato\" = 1 AND \"Destino\" = 'RabbitMq' AND \"Ambito\" = 'Envio' AND \"EstadoEntrega\" IS NULL) OR (\"Tipo\" = 'EnvioCreado' AND \"VersionContrato\" = 1 AND \"Destino\" = 'Kafka' AND \"Ambito\" = 'Envio' AND \"EstadoEntrega\" IS NULL) OR (\"Tipo\" = 'RepartoModificado' AND \"VersionContrato\" = 1 AND \"Destino\" = 'PushFcm' AND \"Ambito\" = 'Envio' AND \"EstadoEntrega\" IS NULL) OR (\"Tipo\" = 'CodigoCancelacionEmail' AND \"VersionContrato\" = 1 AND \"Destino\" = 'RabbitMq' AND \"Ambito\" = 'Envio' AND \"EstadoEntrega\" IS NULL) OR (\"Tipo\" = 'PasswordRecoveryEmail' AND \"VersionContrato\" = 1 AND \"Destino\" = 'RabbitMq' AND \"Ambito\" = 'Identidad' AND \"EstadoEntrega\" IN ('Pending', 'Delivered', 'Expired', 'Failed')) OR (\"Tipo\" = 'PasswordRecoveryRequested' AND \"VersionContrato\" = 1 AND \"Destino\" = 'Kafka' AND \"Ambito\" = 'Identidad' AND \"EstadoEntrega\" IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_CancelacionesEnvio_OperadorId_EnvioId_AutorizacionPublicaId",
                table: "CancelacionesEnvio",
                columns: new[] { "OperadorId", "EnvioId", "AutorizacionPublicaId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_CancelacionesEnvio_Actor",
                table: "CancelacionesEnvio",
                sql: "(\"AutorizacionPublicaId\" IS NULL AND \"AccesoId\" IS NOT NULL AND \"UsuarioId\" IS NOT NULL AND \"TipoActor\" IS NOT NULL AND \"OrganizacionActorId\" IS NOT NULL) OR (\"AutorizacionPublicaId\" IS NOT NULL AND \"AccesoId\" IS NULL AND \"UsuarioId\" IS NULL AND \"TipoActor\" IS NULL AND \"OrganizacionActorId\" IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_CodigosCancelacionPublica_MensajeId",
                table: "CodigosCancelacionPublica",
                column: "MensajeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CodigosCancelacionPublica_OperadorId_EnvioId",
                table: "CodigosCancelacionPublica",
                columns: new[] { "OperadorId", "EnvioId" },
                unique: true,
                filter: "\"Activo\" = TRUE");

            migrationBuilder.AddForeignKey(
                name: "FK_CancelacionesEnvio_CodigosCancelacionPublica_OperadorId_Env~",
                table: "CancelacionesEnvio",
                columns: new[] { "OperadorId", "EnvioId", "AutorizacionPublicaId" },
                principalTable: "CodigosCancelacionPublica",
                principalColumns: new[] { "OperadorId", "EnvioId", "Id" },
                onDelete: ReferentialAction.Restrict);
            ConfigurePublicCancellation(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN IF EXISTS(SELECT 1 FROM public."CancelacionesEnvio" WHERE "AutorizacionPublicaId" IS NOT NULL) THEN
                    RAISE EXCEPTION 'Cannot remove verified recipient audit facts.' USING ERRCODE='55000';
                END IF; END $$;
                """);
            RemovePublicCancellation(migrationBuilder);
            migrationBuilder.DropForeignKey(
                name: "FK_CancelacionesEnvio_CodigosCancelacionPublica_OperadorId_Env~",
                table: "CancelacionesEnvio");

            migrationBuilder.DropTable(
                name: "CodigosCancelacionPublica");

            migrationBuilder.DropCheckConstraint(
                name: "CK_OutboxMessages_Contract",
                table: "OutboxMessages");

            migrationBuilder.DropIndex(
                name: "IX_CancelacionesEnvio_OperadorId_EnvioId_AutorizacionPublicaId",
                table: "CancelacionesEnvio");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CancelacionesEnvio_Actor",
                table: "CancelacionesEnvio");

            migrationBuilder.DropColumn(
                name: "AutorizacionPublicaId",
                table: "CancelacionesEnvio");

            migrationBuilder.AlterColumn<Guid>(
                name: "UsuarioId",
                table: "CancelacionesEnvio",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "TipoActor",
                table: "CancelacionesEnvio",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "OrganizacionActorId",
                table: "CancelacionesEnvio",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "AccesoId",
                table: "CancelacionesEnvio",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_OutboxMessages_Contract",
                table: "OutboxMessages",
                sql: "(\"Tipo\" = 'GenerarSeguimiento' AND \"VersionContrato\" = 1 AND \"Destino\" = 'RabbitMq' AND \"Ambito\" = 'Envio' AND \"EstadoEntrega\" IS NULL) OR (\"Tipo\" = 'EnvioCreado' AND \"VersionContrato\" = 1 AND \"Destino\" = 'Kafka' AND \"Ambito\" = 'Envio' AND \"EstadoEntrega\" IS NULL) OR (\"Tipo\" = 'RepartoModificado' AND \"VersionContrato\" = 1 AND \"Destino\" = 'PushFcm' AND \"Ambito\" = 'Envio' AND \"EstadoEntrega\" IS NULL) OR (\"Tipo\" = 'PasswordRecoveryEmail' AND \"VersionContrato\" = 1 AND \"Destino\" = 'RabbitMq' AND \"Ambito\" = 'Identidad' AND \"EstadoEntrega\" IN ('Pending', 'Delivered', 'Expired', 'Failed')) OR (\"Tipo\" = 'PasswordRecoveryRequested' AND \"VersionContrato\" = 1 AND \"Destino\" = 'Kafka' AND \"Ambito\" = 'Identidad' AND \"EstadoEntrega\" IS NULL)");
        }
    }
}
