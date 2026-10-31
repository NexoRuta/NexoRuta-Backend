using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConfirmOperationalShipmentLoss : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConfirmacionesExtravioEnvio",
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
                    ConfirmadaEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfirmacionesExtravioEnvio", x => x.Id);
                    table.UniqueConstraint("AK_ConfirmacionesExtravioEnvio_OperadorId_Id", x => new { x.OperadorId, x.Id });
                    table.CheckConstraint("CK_ConfirmacionesExtravioEnvio_Values", "\"VersionAnterior\" >= 0 AND \"VersionNueva\" = \"VersionAnterior\" + 1 AND char_length(btrim(\"Motivo\")) BETWEEN 1 AND 500 AND \"OrganizacionId\" = \"OperadorId\"");
                    table.ForeignKey(
                        name: "FK_ConfirmacionesExtravioEnvio_Envios_OperadorId_EnvioId",
                        columns: x => new { x.OperadorId, x.EnvioId },
                        principalTable: "Envios",
                        principalColumns: new[] { "OperadorId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConfirmacionesExtravioEnvio_VersionesReglasOperativas_Opera~",
                        columns: x => new { x.OperadorId, x.VersionReglasId },
                        principalTable: "VersionesReglasOperativas",
                        principalColumns: new[] { "OperadorId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConfirmacionesExtravioEnvio_OperadorId_ComandoId",
                table: "ConfirmacionesExtravioEnvio",
                columns: new[] { "OperadorId", "ComandoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConfirmacionesExtravioEnvio_OperadorId_EnvioId",
                table: "ConfirmacionesExtravioEnvio",
                columns: new[] { "OperadorId", "EnvioId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConfirmacionesExtravioEnvio_OperadorId_VersionReglasId",
                table: "ConfirmacionesExtravioEnvio",
                columns: new[] { "OperadorId", "VersionReglasId" });

            migrationBuilder.Sql("""
                ALTER TABLE public."ConfirmacionesExtravioEnvio" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."ConfirmacionesExtravioEnvio" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."ConfirmacionesExtravioEnvio" FORCE ROW LEVEL SECURITY;
                REVOKE ALL ON public."ConfirmacionesExtravioEnvio" FROM PUBLIC, nexoruta_api, nexoruta_worker;
                GRANT SELECT, INSERT ON public."ConfirmacionesExtravioEnvio" TO nexoruta_api;

                CREATE POLICY lost_shipment_confirmation_select ON public."ConfirmacionesExtravioEnvio"
                    FOR SELECT TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind') = 'operador'
                        AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY lost_shipment_confirmation_insert ON public."ConfirmacionesExtravioEnvio"
                    FOR INSERT TO nexoruta_api
                    WITH CHECK (nexoruta_app.setting('actor_kind') = 'operador'
                        AND "OperadorId" = nexoruta_app.ctx_uuid('org_id')
                        AND "OrganizacionId" = nexoruta_app.ctx_uuid('org_id')
                        AND "UsuarioId" = nexoruta_app.ctx_uuid('user_id')
                        AND "AccesoId" = nexoruta_app.ctx_uuid('access_id'));

                CREATE FUNCTION public.prevent_lost_shipment_confirmation_mutation()
                RETURNS trigger
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog
                AS $immutable$
                BEGIN
                    RAISE EXCEPTION 'Lost shipment confirmations are append-only.' USING ERRCODE = '42501';
                END
                $immutable$;
                ALTER FUNCTION public.prevent_lost_shipment_confirmation_mutation() OWNER TO nexoruta_schema_owner;
                REVOKE ALL ON FUNCTION public.prevent_lost_shipment_confirmation_mutation() FROM PUBLIC, nexoruta_api, nexoruta_worker;
                CREATE TRIGGER lost_shipment_confirmation_append_only
                    BEFORE UPDATE OR DELETE ON public."ConfirmacionesExtravioEnvio"
                    FOR EACH ROW EXECUTE FUNCTION public.prevent_lost_shipment_confirmation_mutation();

                -- Reserve the stable dispatcher code without assigning any user to it.
                INSERT INTO public."Permisos" ("Id", "Codigo", "Nombre")
                VALUES (gen_random_uuid(), 'envios.gestionar', 'envios.gestionar'),
                       (gen_random_uuid(), 'envios.consultar', 'envios.consultar')
                ON CONFLICT ("Codigo") DO NOTHING;

                DO $provision_dispatchers$
                BEGIN
                    IF EXISTS (SELECT 1 FROM public."Roles" WHERE "OperadorId" IS NOT NULL
                        AND "Codigo" = 'despachador_operador' AND NOT "EsProtegidoSistema") THEN
                        RAISE EXCEPTION 'The reserved dispatcher role code is already used by an unprotected role.';
                    END IF;
                END
                $provision_dispatchers$;

                INSERT INTO public."Roles"
                    ("Id", "Codigo", "Nombre", "OperadorId", "ComercioId", "TipoOrganizacion", "EsProtegidoSistema")
                SELECT gen_random_uuid(), 'despachador_operador', 'Dispatcher', o."Id", NULL, 'Operador', TRUE
                FROM public."Operadores" o
                WHERE NOT EXISTS (SELECT 1 FROM public."Roles" r
                    WHERE r."OperadorId" = o."Id" AND r."Codigo" = 'despachador_operador');

                INSERT INTO public."RolesPermisos" ("RolId", "PermisoId")
                SELECT r."Id", p."Id"
                FROM public."Roles" r
                JOIN public."Permisos" p ON p."Codigo" IN ('envios.gestionar', 'envios.consultar')
                WHERE r."OperadorId" IS NOT NULL AND r."Codigo" = 'despachador_operador'
                    AND r."EsProtegidoSistema"
                ON CONFLICT ("RolId", "PermisoId") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS lost_shipment_confirmation_append_only ON public."ConfirmacionesExtravioEnvio";
                DROP FUNCTION IF EXISTS public.prevent_lost_shipment_confirmation_mutation();
                DROP POLICY IF EXISTS lost_shipment_confirmation_select ON public."ConfirmacionesExtravioEnvio";
                DROP POLICY IF EXISTS lost_shipment_confirmation_insert ON public."ConfirmacionesExtravioEnvio";
                """);
            migrationBuilder.DropTable(
                name: "ConfirmacionesExtravioEnvio");
        }
    }
}
