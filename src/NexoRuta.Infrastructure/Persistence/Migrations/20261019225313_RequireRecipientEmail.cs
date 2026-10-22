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
                -- Validate every row as the table owner. The FORCE setting is
                -- restored in this statement; an error rolls back the change.
                DO $email$
                DECLARE was_forced boolean;
                BEGIN
                    SELECT relforcerowsecurity INTO was_forced FROM pg_class
                    WHERE oid = 'public."Destinatarios"'::regclass;
                    IF was_forced THEN
                        ALTER TABLE public."Destinatarios" NO FORCE ROW LEVEL SECURITY;
                    END IF;
                    IF EXISTS (SELECT 1 FROM public."Destinatarios" WHERE "Email" IS NULL OR "Email" <> btrim("Email")
                        OR "Email" !~ '^[^[:space:]@]+@[^[:space:]@]+$') THEN
                        RAISE EXCEPTION 'Complete recipient emails before making them mandatory.' USING ERRCODE = '23514';
                    END IF;
                    IF was_forced THEN
                        ALTER TABLE public."Destinatarios" FORCE ROW LEVEL SECURITY;
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
