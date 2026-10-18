using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

using NexoRuta.Infrastructure.Outbox;

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPasswordRecoverySecurityOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SecurityOutboxMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Destination = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessorId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DeliveryState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityOutboxMessages", x => x.Id);
                    table.CheckConstraint("CK_SecurityOutboxMessages_Contract", "(\"Type\" = 'PasswordRecoveryEmail' AND \"Destination\" = 'RabbitMq' AND \"DeliveryState\" IN ('Pending', 'Delivered', 'Expired', 'Failed')) OR (\"Type\" = 'PasswordRecoveryRequested' AND \"Destination\" = 'Kafka' AND \"DeliveryState\" IS NULL)");
                });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityOutboxMessages_Destination_State_NextAttemptAtUtc",
                table: "SecurityOutboxMessages",
                columns: new[] { "Destination", "State", "NextAttemptAtUtc" });

            migrationBuilder.Sql("UPDATE public.\"Usuarios\" SET \"SecurityStamp\" = gen_random_uuid()::text WHERE \"SecurityStamp\" IS NULL;");
            migrationBuilder.Sql(SecurityOutboxMigrationSql.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SecurityOutboxMigrationSql.Down);
            migrationBuilder.DropTable(
                name: "SecurityOutboxMessages");
        }
    }
}
