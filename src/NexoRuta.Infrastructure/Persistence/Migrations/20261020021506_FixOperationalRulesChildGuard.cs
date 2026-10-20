using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FixOperationalRulesChildGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION nexoruta_app.guard_operational_rules_children()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public
                AS $function$
                DECLARE
                    parent_state text;
                BEGIN
                    IF TG_TABLE_NAME = 'MotivosNoEntregaVersion' AND TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'Motive codes cannot be deleted; disable availability instead' USING ERRCODE = '55000';
                    END IF;
                    IF TG_OP <> 'INSERT' THEN
                        SELECT "Estado" INTO parent_state FROM public."VersionesReglasOperativas"
                         WHERE "OperadorId" = OLD."OperadorId" AND "Id" = OLD."VersionId";
                        IF parent_state = 'Publicada' THEN
                            RAISE EXCEPTION 'Published operational rules children are immutable' USING ERRCODE = '55000';
                        END IF;
                        IF TG_OP = 'UPDATE' AND TG_TABLE_NAME = 'MotivosNoEntregaVersion' THEN
                            IF NEW."OperadorId" IS DISTINCT FROM OLD."OperadorId"
                               OR NEW."VersionId" IS DISTINCT FROM OLD."VersionId"
                               OR to_jsonb(NEW)->>'Codigo' IS DISTINCT FROM to_jsonb(OLD)->>'Codigo' THEN
                                RAISE EXCEPTION 'Motive code identity is immutable' USING ERRCODE = '55000';
                            END IF;
                        END IF;
                        IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
                    END IF;
                    SELECT "Estado" INTO parent_state FROM public."VersionesReglasOperativas"
                     WHERE "OperadorId" = NEW."OperadorId" AND "Id" = NEW."VersionId";
                    IF parent_state IS DISTINCT FROM 'Borrador' THEN
                        RAISE EXCEPTION 'Operational rules children can only be written in a draft' USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END
                $function$;
                ALTER FUNCTION nexoruta_app.guard_operational_rules_children() OWNER TO nexoruta_schema_owner;
                REVOKE ALL ON FUNCTION nexoruta_app.guard_operational_rules_children() FROM PUBLIC, nexoruta_api, nexoruta_worker, nexoruta_bootstrap;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION nexoruta_app.guard_operational_rules_children()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public
                AS $function$
                DECLARE
                    parent_state text;
                BEGIN
                    IF TG_TABLE_NAME = 'MotivosNoEntregaVersion' AND TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'Motive codes cannot be deleted; disable availability instead' USING ERRCODE = '55000';
                    END IF;
                    IF TG_OP <> 'INSERT' THEN
                        SELECT "Estado" INTO parent_state FROM public."VersionesReglasOperativas"
                         WHERE "OperadorId" = OLD."OperadorId" AND "Id" = OLD."VersionId";
                        IF parent_state = 'Publicada' THEN
                            RAISE EXCEPTION 'Published operational rules children are immutable' USING ERRCODE = '55000';
                        END IF;
                        IF TG_TABLE_NAME = 'MotivosNoEntregaVersion' AND TG_OP = 'UPDATE'
                           AND (NEW."OperadorId" <> OLD."OperadorId" OR NEW."VersionId" <> OLD."VersionId"
                                OR NEW."Codigo" <> OLD."Codigo") THEN
                            RAISE EXCEPTION 'Motive code identity is immutable' USING ERRCODE = '55000';
                        END IF;
                        IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
                    END IF;
                    SELECT "Estado" INTO parent_state FROM public."VersionesReglasOperativas"
                     WHERE "OperadorId" = NEW."OperadorId" AND "Id" = NEW."VersionId";
                    IF parent_state IS DISTINCT FROM 'Borrador' THEN
                        RAISE EXCEPTION 'Operational rules children can only be written in a draft' USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END
                $function$;
                ALTER FUNCTION nexoruta_app.guard_operational_rules_children() OWNER TO nexoruta_schema_owner;
                REVOKE ALL ON FUNCTION nexoruta_app.guard_operational_rules_children() FROM PUBLIC, nexoruta_api, nexoruta_worker, nexoruta_bootstrap;
                """);
        }
    }
}
