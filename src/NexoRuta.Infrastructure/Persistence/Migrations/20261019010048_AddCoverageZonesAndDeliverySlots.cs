using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCoverageZonesAndDeliverySlots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:postgis", ",,");

            migrationBuilder.CreateTable(
                name: "DepartamentosReferencia",
                columns: table => new
                {
                    Codigo = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    VersionFuente = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Nombre = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Geometria = table.Column<MultiPolygon>(type: "geometry(MultiPolygon,4326)", nullable: false),
                    UrlFuente = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Atribucion = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Licencia = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    ImportadaUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DepartamentosReferencia", x => new { x.Codigo, x.VersionFuente });
                    table.CheckConstraint("CK_DepartamentosReferencia_Geometria", "NOT ST_IsEmpty(\"Geometria\") AND ST_NDims(\"Geometria\") = 2 AND ST_IsValid(\"Geometria\") AND ST_XMin(Box3D(\"Geometria\")) >= -180 AND ST_XMax(Box3D(\"Geometria\")) <= 180 AND ST_YMin(Box3D(\"Geometria\")) >= -90 AND ST_YMax(Box3D(\"Geometria\")) <= 90");
                });

            migrationBuilder.CreateTable(
                name: "ZonasCobertura",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Nombre = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Estado = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Geometria = table.Column<MultiPolygon>(type: "geometry(MultiPolygon,4326)", nullable: false),
                    ZonaHoraria = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    RevisionActual = table.Column<int>(type: "integer", nullable: false),
                    OrigenGeometria = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CodigoDepartamento = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    VersionFuente = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    CreadaUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActualizadaUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ZonasCobertura", x => x.Id);
                    table.UniqueConstraint("AK_ZonasCobertura_OperadorId_Id", x => new { x.OperadorId, x.Id });
                    table.CheckConstraint("CK_ZonasCobertura_Geometria", "NOT ST_IsEmpty(\"Geometria\") AND ST_NDims(\"Geometria\") = 2 AND ST_IsValid(\"Geometria\") AND ST_XMin(Box3D(\"Geometria\")) >= -180 AND ST_XMax(Box3D(\"Geometria\")) <= 180 AND ST_YMin(Box3D(\"Geometria\")) >= -90 AND ST_YMax(Box3D(\"Geometria\")) <= 90");
                    table.CheckConstraint("CK_ZonasCobertura_NameTimeZone", "length(btrim(\"Nombre\")) > 0 AND \"ZonaHoraria\" = 'America/Montevideo'");
                    table.CheckConstraint("CK_ZonasCobertura_Origin", "(\"OrigenGeometria\" = 'Dibujada' AND \"CodigoDepartamento\" IS NULL AND \"VersionFuente\" IS NULL) OR (\"OrigenGeometria\" = 'Departamento' AND \"CodigoDepartamento\" IS NOT NULL AND \"VersionFuente\" IS NOT NULL)");
                    table.CheckConstraint("CK_ZonasCobertura_State", "\"Estado\" IN ('Inactiva', 'Activa')");
                    table.CheckConstraint("CK_ZonasCobertura_Version", "\"Version\" >= 0 AND \"RevisionActual\" > 0");
                    table.ForeignKey(
                        name: "FK_ZonasCobertura_DepartamentosReferencia_CodigoDepartamento_V~",
                        columns: x => new { x.CodigoDepartamento, x.VersionFuente },
                        principalTable: "DepartamentosReferencia",
                        principalColumns: new[] { "Codigo", "VersionFuente" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ZonasCobertura_Operadores_OperadorId",
                        column: x => x.OperadorId,
                        principalTable: "Operadores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FranjasEntrega",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    ZonaId = table.Column<Guid>(type: "uuid", nullable: false),
                    DiaSemanaIso = table.Column<short>(type: "smallint", nullable: false),
                        HoraInicio = table.Column<TimeOnly>(type: "time(6) without time zone", nullable: false),
                        HoraFin = table.Column<TimeOnly>(type: "time(6) without time zone", nullable: false),
                    Estado = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FranjasEntrega", x => x.Id);
                    table.CheckConstraint("CK_FranjasEntrega_Day", "\"DiaSemanaIso\" BETWEEN 1 AND 7");
                    table.CheckConstraint("CK_FranjasEntrega_State", "\"Estado\" IN ('Inactiva', 'Activa')");
                    table.CheckConstraint("CK_FranjasEntrega_Time", "\"HoraInicio\" <> \"HoraFin\"");
                    table.ForeignKey(
                        name: "FK_FranjasEntrega_ZonasCobertura_OperadorId_ZonaId",
                        columns: x => new { x.OperadorId, x.ZonaId },
                        principalTable: "ZonasCobertura",
                        principalColumns: new[] { "OperadorId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ZonasCoberturaRevisiones",
                columns: table => new
                {
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    ZonaId = table.Column<Guid>(type: "uuid", nullable: false),
                    NumeroRevision = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    RegistradaUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    SnapshotJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ZonasCoberturaRevisiones", x => new { x.OperadorId, x.ZonaId, x.NumeroRevision });
                    table.CheckConstraint("CK_ZonasCoberturaRevisiones_Revision", "\"NumeroRevision\" > 0 AND \"Version\" >= 0");
                    table.CheckConstraint("CK_ZonasCoberturaRevisiones_Snapshot", "jsonb_typeof(\"SnapshotJson\") = 'object'");
                    table.ForeignKey(
                        name: "FK_ZonasCoberturaRevisiones_ZonasCobertura_OperadorId_ZonaId",
                        columns: x => new { x.OperadorId, x.ZonaId },
                        principalTable: "ZonasCobertura",
                        principalColumns: new[] { "OperadorId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DepartamentosReferencia_Geometria_Gist",
                table: "DepartamentosReferencia",
                column: "Geometria")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "IX_FranjasEntrega_OperadorId_ZonaId_DiaSemanaIso_HoraInicio",
                table: "FranjasEntrega",
                columns: new[] { "OperadorId", "ZonaId", "DiaSemanaIso", "HoraInicio" });

            migrationBuilder.CreateIndex(
                name: "IX_ZonasCobertura_CodigoDepartamento_VersionFuente",
                table: "ZonasCobertura",
                columns: new[] { "CodigoDepartamento", "VersionFuente" });

            migrationBuilder.CreateIndex(
                name: "IX_ZonasCobertura_Geometria_Gist",
                table: "ZonasCobertura",
                column: "Geometria")
                .Annotation("Npgsql:IndexMethod", "gist");

            migrationBuilder.CreateIndex(
                name: "IX_ZonasCobertura_OperadorId_Estado",
                table: "ZonasCobertura",
                columns: new[] { "OperadorId", "Estado" });

            migrationBuilder.Sql("""
                ALTER TABLE public."ZonasCobertura" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."FranjasEntrega" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."ZonasCoberturaRevisiones" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."DepartamentosReferencia" OWNER TO nexoruta_schema_owner;

                ALTER TABLE public."ZonasCobertura" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."ZonasCobertura" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."FranjasEntrega" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."FranjasEntrega" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."ZonasCoberturaRevisiones" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."ZonasCoberturaRevisiones" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."DepartamentosReferencia" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."DepartamentosReferencia" FORCE ROW LEVEL SECURITY;

                REVOKE ALL ON public."ZonasCobertura", public."FranjasEntrega", public."ZonasCoberturaRevisiones",
                    public."DepartamentosReferencia" FROM PUBLIC, nexoruta_api, nexoruta_worker, nexoruta_bootstrap;
                GRANT SELECT, INSERT, UPDATE ON public."ZonasCobertura", public."FranjasEntrega" TO nexoruta_api;
                GRANT SELECT, INSERT ON public."ZonasCoberturaRevisiones" TO nexoruta_api;
                GRANT SELECT ON public."DepartamentosReferencia" TO nexoruta_api;
                REVOKE ALL ON public.spatial_ref_sys FROM PUBLIC, nexoruta_api, nexoruta_worker, nexoruta_bootstrap;

                CREATE POLICY coverage_zone_operator_select ON public."ZonasCobertura" FOR SELECT TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY coverage_zone_operator_insert ON public."ZonasCobertura" FOR INSERT TO nexoruta_api
                    WITH CHECK (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY coverage_zone_operator_update ON public."ZonasCobertura" FOR UPDATE TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'))
                    WITH CHECK (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));

                CREATE POLICY delivery_slot_operator_select ON public."FranjasEntrega" FOR SELECT TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY delivery_slot_operator_insert ON public."FranjasEntrega" FOR INSERT TO nexoruta_api
                    WITH CHECK (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY delivery_slot_operator_update ON public."FranjasEntrega" FOR UPDATE TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'))
                    WITH CHECK (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));

                CREATE POLICY coverage_revision_operator_select ON public."ZonasCoberturaRevisiones" FOR SELECT TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY coverage_revision_operator_insert ON public."ZonasCoberturaRevisiones" FOR INSERT TO nexoruta_api
                    WITH CHECK (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id')
                        AND "UsuarioId" = nexoruta_app.ctx_uuid('user_id'));
                CREATE POLICY department_reference_operator_select ON public."DepartamentosReferencia" FOR SELECT TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind') = 'operador' AND nexoruta_app.ctx_uuid('org_id') IS NOT NULL);

                CREATE FUNCTION nexoruta_app.reject_coverage_revision_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                SET search_path = pg_catalog
                AS $function$
                BEGIN
                    RAISE EXCEPTION 'Coverage history is immutable' USING ERRCODE = '55000';
                END
                $function$;
                ALTER FUNCTION nexoruta_app.reject_coverage_revision_mutation() OWNER TO nexoruta_schema_owner;
                REVOKE ALL ON FUNCTION nexoruta_app.reject_coverage_revision_mutation() FROM PUBLIC, nexoruta_api,
                    nexoruta_worker, nexoruta_bootstrap;
                CREATE TRIGGER coverage_revision_immutable
                    BEFORE UPDATE OR DELETE ON public."ZonasCoberturaRevisiones"
                    FOR EACH ROW EXECUTE FUNCTION nexoruta_app.reject_coverage_revision_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS coverage_revision_immutable ON public."ZonasCoberturaRevisiones";
                DROP FUNCTION IF EXISTS nexoruta_app.reject_coverage_revision_mutation();
                """);

            migrationBuilder.DropTable(
                name: "FranjasEntrega");

            migrationBuilder.DropTable(
                name: "ZonasCoberturaRevisiones");

            migrationBuilder.DropTable(
                name: "ZonasCobertura");

            migrationBuilder.DropTable(
                name: "DepartamentosReferencia");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:postgis", ",,");
        }
    }
}
