using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOperationalBaseAndTemporalPlan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ServicioMinutos",
                table: "ParadasRuta",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlanTemporal",
                table: "HojasRuta",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BasesOperativas",
                columns: table => new
                {
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Direccion = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Latitud = table.Column<double>(type: "double precision", nullable: true),
                    Longitud = table.Column<double>(type: "double precision", nullable: true),
                    ConfirmadaEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConfirmadaPorAccesoId = table.Column<Guid>(type: "uuid", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    ServicioPredeterminadoMinutos = table.Column<int>(type: "integer", nullable: true),
                    MargenRetornoMinutos = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BasesOperativas", x => x.OperadorId);
                    table.CheckConstraint("CK_Base_Punto", "(\"Latitud\" IS NULL AND \"Longitud\" IS NULL AND \"ConfirmadaEnUtc\" IS NULL AND \"ConfirmadaPorAccesoId\" IS NULL) OR (\"Latitud\" IS NOT NULL AND \"Longitud\" IS NOT NULL AND \"Latitud\" BETWEEN -90 AND 90 AND \"Longitud\" BETWEEN -180 AND 180 AND \"ConfirmadaEnUtc\" IS NOT NULL AND \"ConfirmadaPorAccesoId\" IS NOT NULL)");
                    table.CheckConstraint("CK_Base_Tiempos", "(\"ServicioPredeterminadoMinutos\" IS NULL OR \"ServicioPredeterminadoMinutos\" >= 0) AND (\"MargenRetornoMinutos\" IS NULL OR \"MargenRetornoMinutos\" >= 0)");
                    table.ForeignKey(
                        name: "FK_BasesOperativas_Operadores_OperadorId",
                        column: x => x.OperadorId,
                        principalTable: "Operadores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_ParadasRuta_Servicio",
                table: "ParadasRuta",
                sql: "\"ServicioMinutos\" IS NULL OR \"ServicioMinutos\" >= 0");
            migrationBuilder.Sql("""
                ALTER TABLE public."BasesOperativas" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."BasesOperativas" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."BasesOperativas" FORCE ROW LEVEL SECURITY;
                REVOKE ALL ON public."BasesOperativas" FROM PUBLIC, nexoruta_api, nexoruta_worker;
                GRANT SELECT, INSERT, UPDATE ON public."BasesOperativas" TO nexoruta_api;
                CREATE POLICY base_select ON public."BasesOperativas" FOR SELECT TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind')='operador' AND "OperadorId"=nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY base_insert ON public."BasesOperativas" FOR INSERT TO nexoruta_api
                    WITH CHECK (nexoruta_app.setting('actor_kind')='operador' AND "OperadorId"=nexoruta_app.ctx_uuid('org_id') AND "ConfirmadaEnUtc" IS NULL);
                CREATE POLICY base_update ON public."BasesOperativas" FOR UPDATE TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind')='operador' AND "OperadorId"=nexoruta_app.ctx_uuid('org_id'))
                    WITH CHECK (nexoruta_app.setting('actor_kind')='operador' AND "OperadorId"=nexoruta_app.ctx_uuid('org_id'));
                CREATE FUNCTION public.guard_operational_base() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $guard$
                BEGIN
                    IF NEW."OperadorId" IS DISTINCT FROM OLD."OperadorId" OR NEW."Version" <> OLD."Version"+1 THEN
                        RAISE EXCEPTION 'Base identity is immutable and updates require the next version.' USING ERRCODE='42501';
                    END IF;
                    IF NEW."Direccion" IS DISTINCT FROM OLD."Direccion" AND NEW."ConfirmadaEnUtc" IS NOT NULL THEN
                        RAISE EXCEPTION 'An address change requires a new explicit point confirmation.' USING ERRCODE='42501';
                    END IF;
                    IF NEW."ConfirmadaEnUtc" IS NOT NULL AND (NEW."Latitud",NEW."Longitud",NEW."ConfirmadaEnUtc",NEW."ConfirmadaPorAccesoId")
                        IS DISTINCT FROM (OLD."Latitud",OLD."Longitud",OLD."ConfirmadaEnUtc",OLD."ConfirmadaPorAccesoId")
                        AND NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname=current_user AND (rolsuper OR rolbypassrls)) AND NEW."ConfirmadaPorAccesoId" IS DISTINCT FROM nexoruta_app.ctx_uuid('access_id') THEN
                        RAISE EXCEPTION 'Point confirmation requires the current actor.' USING ERRCODE='42501';
                    END IF;
                    RETURN NEW;
                END $guard$;
                ALTER FUNCTION public.guard_operational_base() OWNER TO nexoruta_schema_owner;
                CREATE TRIGGER operational_base_guard BEFORE UPDATE ON public."BasesOperativas" FOR EACH ROW EXECUTE FUNCTION public.guard_operational_base();
                CREATE FUNCTION public.preserve_temporal_plan() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $immutable$
                BEGIN
                    IF NEW."PlanTemporal" IS DISTINCT FROM OLD."PlanTemporal" THEN
                        RAISE EXCEPTION 'Initial temporal snapshots are immutable.' USING ERRCODE='42501';
                    END IF;
                    RETURN NEW;
                END $immutable$;
                ALTER FUNCTION public.preserve_temporal_plan() OWNER TO nexoruta_schema_owner;
                CREATE TRIGGER temporal_plan_immutable BEFORE UPDATE OF "PlanTemporal" ON public."HojasRuta" FOR EACH ROW EXECUTE FUNCTION public.preserve_temporal_plan();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $guard$ BEGIN
                    IF EXISTS (SELECT 1 FROM public."BasesOperativas") OR EXISTS (SELECT 1 FROM public."HojasRuta" WHERE "PlanTemporal" IS NOT NULL)
                        OR EXISTS (SELECT 1 FROM public."ParadasRuta" WHERE "ServicioMinutos" IS NOT NULL) THEN
                        RAISE EXCEPTION 'B3 data exists; rollback needs a deliberate data-preserving procedure.';
                    END IF;
                END $guard$;
                DROP TRIGGER temporal_plan_immutable ON public."HojasRuta";
                DROP FUNCTION public.preserve_temporal_plan();
                DROP FUNCTION public.guard_operational_base() CASCADE;
                """);
            migrationBuilder.DropTable(
                name: "BasesOperativas");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ParadasRuta_Servicio",
                table: "ParadasRuta");

            migrationBuilder.DropColumn(
                name: "ServicioMinutos",
                table: "ParadasRuta");

            migrationBuilder.DropColumn(
                name: "PlanTemporal",
                table: "HojasRuta");
        }
    }
}
