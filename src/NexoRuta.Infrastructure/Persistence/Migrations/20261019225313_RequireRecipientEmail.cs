using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RequireRecipientEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $email$ BEGIN
                    IF EXISTS (SELECT 1 FROM public."Destinatarios" WHERE "Email" IS NULL OR "Email" <> btrim("Email")
                        OR "Email" !~ '^[^[:space:]@]+@[^[:space:]@]+$') THEN
                        RAISE EXCEPTION 'Complete recipient emails before making them mandatory.' USING ERRCODE = '23514';
                    END IF;
                END $email$;
                """);
            migrationBuilder.AlterColumn<string>(
                name: "Email",
                table: "Destinatarios",
                type: "character varying(254)",
                maxLength: 254,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(254)",
                oldMaxLength: 254,
                oldNullable: true);
            ConfigureEmailInput(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Email",
                table: "Destinatarios",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(254)",
                oldMaxLength: 254);
            RestoreEmailInput(migrationBuilder);
        }
    }
}
