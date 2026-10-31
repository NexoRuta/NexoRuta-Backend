using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProofValidationDeadline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PlazoValidacionPruebaMinutos",
                table: "VersionesReglasOperativas",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_VersionesReglasOperativas_ProofValidationDeadline",
                table: "VersionesReglasOperativas",
                sql: "\"PlazoValidacionPruebaMinutos\" IS NULL OR \"PlazoValidacionPruebaMinutos\" BETWEEN 1 AND 525600");

            migrationBuilder.Sql("""
                CREATE FUNCTION nexoruta_app.require_proof_deadline_on_publication()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public
                AS $function$
                BEGIN
                    IF OLD."Estado" = 'Borrador' AND NEW."Estado" = 'Publicada'
                       AND NEW."PlazoValidacionPruebaMinutos" IS NULL THEN
                        RAISE EXCEPTION 'A new operational-rules publication requires a proof-validation deadline'
                            USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END
                $function$;
                ALTER FUNCTION nexoruta_app.require_proof_deadline_on_publication() OWNER TO nexoruta_schema_owner;
                REVOKE ALL ON FUNCTION nexoruta_app.require_proof_deadline_on_publication()
                    FROM PUBLIC, nexoruta_api, nexoruta_worker, nexoruta_bootstrap;
                CREATE TRIGGER operational_rules_proof_deadline_required
                    BEFORE UPDATE OF "Estado" ON public."VersionesReglasOperativas"
                    FOR EACH ROW EXECUTE FUNCTION nexoruta_app.require_proof_deadline_on_publication();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS operational_rules_proof_deadline_required ON public."VersionesReglasOperativas";
                DROP FUNCTION IF EXISTS nexoruta_app.require_proof_deadline_on_publication();
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_VersionesReglasOperativas_ProofValidationDeadline",
                table: "VersionesReglasOperativas");

            migrationBuilder.DropColumn(
                name: "PlazoValidacionPruebaMinutos",
                table: "VersionesReglasOperativas");
        }
    }
}
