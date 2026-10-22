using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveDuplicateCourierRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ConfiguracionesReparto_Limites",
                table: "ConfiguracionesReparto");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ConfiguracionesReparto_Motivos",
                table: "ConfiguracionesReparto");

            migrationBuilder.DropColumn(
                name: "MaximoIntentos",
                table: "ConfiguracionesReparto");

            migrationBuilder.DropColumn(
                name: "Motivos",
                table: "ConfiguracionesReparto");

            migrationBuilder.DropColumn(
                name: "RequiereDocumento",
                table: "ConfiguracionesReparto");

            migrationBuilder.DropColumn(
                name: "RequiereFirma",
                table: "ConfiguracionesReparto");

            migrationBuilder.DropColumn(
                name: "RequiereFoto",
                table: "ConfiguracionesReparto");

            migrationBuilder.DropColumn(
                name: "RequiereNombre",
                table: "ConfiguracionesReparto");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ConfiguracionesReparto_Limites",
                table: "ConfiguracionesReparto",
                sql: "\"MaximoParadas\" BETWEEN 1 AND 500");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $guard$
                DECLARE was_forced boolean;
                BEGIN
                    SELECT relforcerowsecurity INTO was_forced FROM pg_class
                    WHERE oid = 'public."ConfiguracionesReparto"'::regclass;
                    IF was_forced THEN
                        ALTER TABLE public."ConfiguracionesReparto" NO FORCE ROW LEVEL SECURITY;
                    END IF;
                    IF EXISTS(SELECT 1 FROM public."ConfiguracionesReparto") THEN
                    RAISE EXCEPTION 'Cannot reconstruct deleted duplicate courier policies. Roll back only an empty development database.' USING ERRCODE='55000';
                    END IF;
                    IF was_forced THEN
                        ALTER TABLE public."ConfiguracionesReparto" FORCE ROW LEVEL SECURITY;
                    END IF;
                END $guard$;
                """);
            migrationBuilder.DropCheckConstraint(
                name: "CK_ConfiguracionesReparto_Limites",
                table: "ConfiguracionesReparto");

            migrationBuilder.AddColumn<int>(
                name: "MaximoIntentos",
                table: "ConfiguracionesReparto",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string[]>(
                name: "Motivos",
                table: "ConfiguracionesReparto",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<bool>(
                name: "RequiereDocumento",
                table: "ConfiguracionesReparto",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RequiereFirma",
                table: "ConfiguracionesReparto",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RequiereFoto",
                table: "ConfiguracionesReparto",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RequiereNombre",
                table: "ConfiguracionesReparto",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ConfiguracionesReparto_Limites",
                table: "ConfiguracionesReparto",
                sql: "\"MaximoParadas\" BETWEEN 1 AND 500 AND \"MaximoIntentos\" BETWEEN 1 AND 100");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ConfiguracionesReparto_Motivos",
                table: "ConfiguracionesReparto",
                sql: "cardinality(\"Motivos\") BETWEEN 1 AND 100");
        }
    }
}
