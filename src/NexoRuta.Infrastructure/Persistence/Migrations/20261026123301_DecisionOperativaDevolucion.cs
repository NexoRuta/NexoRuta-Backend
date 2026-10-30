using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DecisionOperativaDevolucion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DecisionesDevolucionEnvio",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnvioId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionReglasId = table.Column<Guid>(type: "uuid", nullable: false),
                    ComandoId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionAnterior = table.Column<long>(type: "bigint", nullable: false),
                    VersionNueva = table.Column<long>(type: "bigint", nullable: false),
                    Motivo = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccesoId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizacionId = table.Column<Guid>(type: "uuid", nullable: false),
                    DecididaEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DecisionesDevolucionEnvio", x => x.Id);
                    table.UniqueConstraint("AK_DecisionesDevolucionEnvio_OperadorId_Id", x => new { x.OperadorId, x.Id });
                    table.CheckConstraint("CK_DecisionesDevolucionEnvio_Values", "\"VersionAnterior\" >= 0 AND \"VersionNueva\" = \"VersionAnterior\" + 1 AND char_length(btrim(\"Motivo\")) BETWEEN 1 AND 500 AND \"OrganizacionId\" = \"OperadorId\"");
                    table.ForeignKey(
                        name: "FK_DecisionesDevolucionEnvio_Envios_OperadorId_EnvioId",
                        columns: x => new { x.OperadorId, x.EnvioId },
                        principalTable: "Envios",
                        principalColumns: new[] { "OperadorId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DecisionesDevolucionEnvio_VersionesReglasOperativas_Operado~",
                        columns: x => new { x.OperadorId, x.VersionReglasId },
                        principalTable: "VersionesReglasOperativas",
                        principalColumns: new[] { "OperadorId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DecisionesDevolucionEnvio_OperadorId_ComandoId",
                table: "DecisionesDevolucionEnvio",
                columns: new[] { "OperadorId", "ComandoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DecisionesDevolucionEnvio_OperadorId_EnvioId_DecididaEnUtc",
                table: "DecisionesDevolucionEnvio",
                columns: new[] { "OperadorId", "EnvioId", "DecididaEnUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DecisionesDevolucionEnvio_OperadorId_VersionReglasId",
                table: "DecisionesDevolucionEnvio",
                columns: new[] { "OperadorId", "VersionReglasId" });

            migrationBuilder.Sql("""
                ALTER TABLE public."DecisionesDevolucionEnvio" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."DecisionesDevolucionEnvio" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."DecisionesDevolucionEnvio" FORCE ROW LEVEL SECURITY;
                REVOKE ALL ON public."DecisionesDevolucionEnvio" FROM PUBLIC, nexoruta_api, nexoruta_worker;
                GRANT SELECT, INSERT ON public."DecisionesDevolucionEnvio" TO nexoruta_api;

                CREATE POLICY operational_return_decision_select ON public."DecisionesDevolucionEnvio"
                    FOR SELECT TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind') = 'operador'
                        AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY operational_return_decision_insert ON public."DecisionesDevolucionEnvio"
                    FOR INSERT TO nexoruta_api
                    WITH CHECK (nexoruta_app.setting('actor_kind') = 'operador'
                        AND "OperadorId" = nexoruta_app.ctx_uuid('org_id')
                        AND "OrganizacionId" = nexoruta_app.ctx_uuid('org_id')
                        AND "UsuarioId" = nexoruta_app.ctx_uuid('user_id')
                        AND "AccesoId" = nexoruta_app.ctx_uuid('access_id'));

                CREATE FUNCTION public.prevent_operational_return_decision_mutation()
                RETURNS trigger
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog
                AS $immutable$
                BEGIN
                    RAISE EXCEPTION 'Operational return decisions are append-only.' USING ERRCODE = '42501';
                END
                $immutable$;
                ALTER FUNCTION public.prevent_operational_return_decision_mutation() OWNER TO nexoruta_schema_owner;
                REVOKE ALL ON FUNCTION public.prevent_operational_return_decision_mutation() FROM PUBLIC, nexoruta_api, nexoruta_worker;
                CREATE TRIGGER operational_return_decision_append_only
                    BEFORE UPDATE OR DELETE ON public."DecisionesDevolucionEnvio"
                    FOR EACH ROW EXECUTE FUNCTION public.prevent_operational_return_decision_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS operational_return_decision_append_only ON public."DecisionesDevolucionEnvio";
                DROP FUNCTION IF EXISTS public.prevent_operational_return_decision_mutation();
                DROP POLICY IF EXISTS operational_return_decision_select ON public."DecisionesDevolucionEnvio";
                DROP POLICY IF EXISTS operational_return_decision_insert ON public."DecisionesDevolucionEnvio";
                """);
            migrationBuilder.DropTable(
                name: "DecisionesDevolucionEnvio");
        }
    }
}
