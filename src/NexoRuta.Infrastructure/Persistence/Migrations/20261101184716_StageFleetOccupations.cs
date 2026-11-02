using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StageFleetOccupations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "VerificacionInicialEnUtc",
                table: "Vehiculos",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "VerificacionInicialPorAccesoId",
                table: "Vehiculos",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OcupacionesRuta",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    RutaId = table.Column<Guid>(type: "uuid", nullable: false),
                    VehiculoId = table.Column<Guid>(type: "uuid", nullable: false),
                    RepartidorAccesoId = table.Column<Guid>(type: "uuid", nullable: false),
                    Legada = table.Column<bool>(type: "boolean", nullable: false),
                    AdquiridaEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AdquiridaPorAccesoId = table.Column<Guid>(type: "uuid", nullable: true),
                    RetornoEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RetornoPorAccesoId = table.Column<Guid>(type: "uuid", nullable: true),
                    LiberadaEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LiberadaPorAccesoId = table.Column<Guid>(type: "uuid", nullable: true),
                    AnuladaEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AnuladaPorAccesoId = table.Column<Guid>(type: "uuid", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OcupacionesRuta", x => x.Id);
                    table.UniqueConstraint("AK_OcupacionesRuta_OperadorId_Id", x => new { x.OperadorId, x.Id });
                    table.CheckConstraint("CK_OcupacionesRuta_Hechos", "\"Version\" >= 0 AND (\"LiberadaEnUtc\" IS NULL OR (\"RetornoEnUtc\" IS NOT NULL AND \"AnuladaEnUtc\" IS NULL))");
                    table.ForeignKey(
                        name: "FK_OcupacionesRuta_HojasRuta_OperadorId_RutaId",
                        columns: x => new { x.OperadorId, x.RutaId },
                        principalTable: "HojasRuta",
                        principalColumns: new[] { "OperadorId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OcupacionesRuta_Repartidores_RepartidorAccesoId_OperadorId",
                        columns: x => new { x.RepartidorAccesoId, x.OperadorId },
                        principalTable: "Repartidores",
                        principalColumns: new[] { "AccesoId", "OperadorId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OcupacionesRuta_Vehiculos_OperadorId_VehiculoId",
                        columns: x => new { x.OperadorId, x.VehiculoId },
                        principalTable: "Vehiculos",
                        principalColumns: new[] { "OperadorId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "HechosFlota",
                columns: table => new
                {
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    ComandoId = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccesoId = table.Column<Guid>(type: "uuid", nullable: false),
                    VehiculoId = table.Column<Guid>(type: "uuid", nullable: false),
                    RutaId = table.Column<Guid>(type: "uuid", nullable: true),
                    OcupacionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Accion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Motivo = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    VersionAnterior = table.Column<long>(type: "bigint", nullable: false),
                    ConfirmadoEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Respuesta = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HechosFlota", x => new { x.OperadorId, x.ComandoId });
                    table.ForeignKey(
                        name: "FK_HechosFlota_OcupacionesRuta_OperadorId_OcupacionId",
                        columns: x => new { x.OperadorId, x.OcupacionId },
                        principalTable: "OcupacionesRuta",
                        principalColumns: new[] { "OperadorId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HechosFlota_Vehiculos_OperadorId_VehiculoId",
                        columns: x => new { x.OperadorId, x.VehiculoId },
                        principalTable: "Vehiculos",
                        principalColumns: new[] { "OperadorId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HechosFlota_OperadorId_OcupacionId",
                table: "HechosFlota",
                columns: new[] { "OperadorId", "OcupacionId" });

            migrationBuilder.CreateIndex(
                name: "IX_HechosFlota_OperadorId_VehiculoId",
                table: "HechosFlota",
                columns: new[] { "OperadorId", "VehiculoId" });

            migrationBuilder.CreateIndex(
                name: "IX_OcupacionesRuta_OperadorId_RutaId",
                table: "OcupacionesRuta",
                columns: new[] { "OperadorId", "RutaId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OcupacionesRuta_OperadorId_VehiculoId",
                table: "OcupacionesRuta",
                columns: new[] { "OperadorId", "VehiculoId" });

            migrationBuilder.CreateIndex(
                name: "IX_OcupacionesRuta_RepartidorAccesoId_OperadorId",
                table: "OcupacionesRuta",
                columns: new[] { "RepartidorAccesoId", "OperadorId" });
            migrationBuilder.Sql("""
                DO $privileged$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                        RAISE EXCEPTION 'Fleet transition requires an authorized migration identity able to inspect every tenant; no RLS-filtered backfill is permitted.';
                    END IF;
                END $privileged$;
                -- These are legacy assignments requiring verification, NOT reconstructed historical dispatch events.
                INSERT INTO public."OcupacionesRuta" ("Id", "OperadorId", "RutaId", "VehiculoId", "RepartidorAccesoId", "Legada", "Version")
                SELECT gen_random_uuid(), "OperadorId", "Id", "VehiculoId", "RepartidorAccesoId", true, 0
                FROM public."HojasRuta" WHERE "Estado" IN ('Despachada', 'EnCurso');

                ALTER TABLE public."OcupacionesRuta" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."HechosFlota" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."OcupacionesRuta" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."OcupacionesRuta" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."HechosFlota" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."HechosFlota" FORCE ROW LEVEL SECURITY;
                REVOKE ALL ON public."OcupacionesRuta", public."HechosFlota" FROM PUBLIC, nexoruta_api, nexoruta_worker;
                GRANT SELECT, INSERT, UPDATE ON public."OcupacionesRuta" TO nexoruta_api;
                GRANT SELECT, INSERT ON public."HechosFlota" TO nexoruta_api;
                CREATE POLICY fleet_occupation_select ON public."OcupacionesRuta" FOR SELECT TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY fleet_occupation_insert ON public."OcupacionesRuta" FOR INSERT TO nexoruta_api
                    WITH CHECK (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id')
                        AND NOT "Legada" AND "AdquiridaEnUtc" IS NOT NULL AND "AdquiridaPorAccesoId" = nexoruta_app.ctx_uuid('access_id'));
                CREATE POLICY fleet_occupation_update ON public."OcupacionesRuta" FOR UPDATE TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'))
                    WITH CHECK (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY fleet_fact_select ON public."HechosFlota" FOR SELECT TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY fleet_fact_insert ON public."HechosFlota" FOR INSERT TO nexoruta_api
                    WITH CHECK (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id')
                        AND "UsuarioId" = nexoruta_app.ctx_uuid('user_id') AND "AccesoId" = nexoruta_app.ctx_uuid('access_id'));

                CREATE FUNCTION public.prevent_fleet_fact_mutation() RETURNS trigger LANGUAGE plpgsql
                    SECURITY DEFINER SET search_path = pg_catalog AS $immutable$
                BEGIN RAISE EXCEPTION 'Fleet confirmations are append-only.' USING ERRCODE = '42501'; END $immutable$;
                ALTER FUNCTION public.prevent_fleet_fact_mutation() OWNER TO nexoruta_schema_owner;
                REVOKE ALL ON FUNCTION public.prevent_fleet_fact_mutation() FROM PUBLIC, nexoruta_api, nexoruta_worker;
                CREATE TRIGGER fleet_fact_append_only BEFORE UPDATE OR DELETE ON public."HechosFlota"
                    FOR EACH ROW EXECUTE FUNCTION public.prevent_fleet_fact_mutation();

                CREATE FUNCTION public.guard_fleet_occupation() RETURNS trigger LANGUAGE plpgsql
                    SET search_path = pg_catalog AS $guard$
                BEGIN
                    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'Fleet occupation history cannot be deleted.' USING ERRCODE='42501'; END IF;
                    IF NOT EXISTS (SELECT 1 FROM public."HojasRuta" h WHERE h."OperadorId"=NEW."OperadorId" AND h."Id"=NEW."RutaId"
                        AND h."VehiculoId"=NEW."VehiculoId" AND h."RepartidorAccesoId"=NEW."RepartidorAccesoId") THEN
                        RAISE EXCEPTION 'Fleet occupation must belong to its route and resources.' USING ERRCODE='23503';
                    END IF;
                    IF TG_OP = 'UPDATE' AND (
                        (NEW."Id",NEW."OperadorId",NEW."RutaId",NEW."VehiculoId",NEW."RepartidorAccesoId",NEW."Legada")
                            IS DISTINCT FROM (OLD."Id",OLD."OperadorId",OLD."RutaId",OLD."VehiculoId",OLD."RepartidorAccesoId",OLD."Legada")
                        OR NEW."Version" <> OLD."Version" + 1
                        OR (OLD."AdquiridaEnUtc" IS NOT NULL AND (NEW."AdquiridaEnUtc",NEW."AdquiridaPorAccesoId") IS DISTINCT FROM (OLD."AdquiridaEnUtc",OLD."AdquiridaPorAccesoId"))
                        OR (OLD."RetornoEnUtc" IS NOT NULL AND (NEW."RetornoEnUtc",NEW."RetornoPorAccesoId") IS DISTINCT FROM (OLD."RetornoEnUtc",OLD."RetornoPorAccesoId"))
                        OR (OLD."LiberadaEnUtc" IS NOT NULL AND (NEW."LiberadaEnUtc",NEW."LiberadaPorAccesoId") IS DISTINCT FROM (OLD."LiberadaEnUtc",OLD."LiberadaPorAccesoId"))
                        OR (OLD."AnuladaEnUtc" IS NOT NULL AND (NEW."AnuladaEnUtc",NEW."AnuladaPorAccesoId") IS DISTINCT FROM (OLD."AnuladaEnUtc",OLD."AnuladaPorAccesoId"))) THEN
                        RAISE EXCEPTION 'Fleet occupation identity and confirmed facts are immutable; use its current version.' USING ERRCODE='42501';
                    END IF;
                    RETURN NEW;
                END $guard$;
                ALTER FUNCTION public.guard_fleet_occupation() OWNER TO nexoruta_schema_owner;
                CREATE TRIGGER fleet_occupation_guard BEFORE INSERT OR UPDATE OR DELETE ON public."OcupacionesRuta"
                    FOR EACH ROW EXECUTE FUNCTION public.guard_fleet_occupation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $guard$ BEGIN
                    IF EXISTS (SELECT 1 FROM public."OcupacionesRuta") OR EXISTS (SELECT 1 FROM public."HechosFlota") THEN
                        RAISE EXCEPTION 'Fleet history exists: rollback requires an explicit data-preserving procedure.';
                    END IF;
                END $guard$;
                DROP FUNCTION IF EXISTS public.guard_fleet_occupation() CASCADE;
                DROP FUNCTION IF EXISTS public.prevent_fleet_fact_mutation() CASCADE;
                """);
            migrationBuilder.DropTable(
                name: "HechosFlota");

            migrationBuilder.DropTable(
                name: "OcupacionesRuta");

            migrationBuilder.DropColumn(
                name: "VerificacionInicialEnUtc",
                table: "Vehiculos");

            migrationBuilder.DropColumn(
                name: "VerificacionInicialPorAccesoId",
                table: "Vehiculos");
        }
    }
}
