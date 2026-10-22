using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTarifarioOperador : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VersionesTarifario",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Estado = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    NumeroVersionPublicada = table.Column<int>(type: "integer", nullable: true),
                    VersionConcurrencia = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    Moneda = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    CreadaUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreadaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    PublicadaUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PublicadaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VersionesTarifario", x => new { x.OperadorId, x.Id });
                    table.CheckConstraint("CK_VersionesTarifario_Currency", "\"Moneda\" = 'UYU'");
                    table.CheckConstraint("CK_VersionesTarifario_Publication", "(\"Estado\" = 'Borrador' AND \"NumeroVersionPublicada\" IS NULL AND \"PublicadaUtc\" IS NULL AND \"PublicadaPorUsuarioId\" IS NULL) OR (\"Estado\" = 'Publicada' AND \"NumeroVersionPublicada\" > 0 AND \"PublicadaUtc\" IS NOT NULL AND \"PublicadaPorUsuarioId\" IS NOT NULL)");
                    table.CheckConstraint("CK_VersionesTarifario_State", "\"Estado\" IN ('Borrador', 'Publicada')");
                    table.CheckConstraint("CK_VersionesTarifario_Version", "\"VersionConcurrencia\" >= 0");
                    table.ForeignKey(
                        name: "FK_VersionesTarifario_Operadores_OperadorId",
                        column: x => x.OperadorId,
                        principalTable: "Operadores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VersionesTarifario_Usuarios_CreadaPorUsuarioId",
                        column: x => x.CreadaPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VersionesTarifario_Usuarios_PublicadaPorUsuarioId",
                        column: x => x.PublicadaPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TarifariosOperador",
                columns: table => new
                {
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionVigenteId = table.Column<Guid>(type: "uuid", nullable: true),
                    UltimoNumeroPublicacion = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    ActualizadoUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TarifariosOperador", x => x.OperadorId);
                    table.CheckConstraint("CK_TarifariosOperador_PublicationCounter", "\"UltimoNumeroPublicacion\" >= 0");
                    table.ForeignKey(
                        name: "FK_TarifariosOperador_Operadores_OperadorId",
                        column: x => x.OperadorId,
                        principalTable: "Operadores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TarifariosOperador_VersionesTarifario_OperadorId_VersionVig~",
                        columns: x => new { x.OperadorId, x.VersionVigenteId },
                        principalTable: "VersionesTarifario",
                        principalColumns: new[] { "OperadorId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TarifasZonaModalidad",
                columns: table => new
                {
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ZonaId = table.Column<Guid>(type: "uuid", nullable: false),
                    Modalidad = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ImporteBaseUyu = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    PrecioPorKilogramoUyu = table.Column<decimal>(type: "numeric(14,6)", precision: 14, scale: 6, nullable: false),
                    PrecioPorMetroCubicoUyu = table.Column<decimal>(type: "numeric(14,6)", precision: 14, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TarifasZonaModalidad", x => new { x.OperadorId, x.VersionId, x.ZonaId, x.Modalidad });
                    table.CheckConstraint("CK_TarifasZonaModalidad_Amounts", "\"ImporteBaseUyu\" >= 0 AND \"PrecioPorKilogramoUyu\" >= 0 AND \"PrecioPorMetroCubicoUyu\" >= 0");
                    table.CheckConstraint("CK_TarifasZonaModalidad_Modality", "\"Modalidad\" IN ('Estandar', 'Urgente')");
                    table.ForeignKey(
                        name: "FK_TarifasZonaModalidad_VersionesTarifario_OperadorId_VersionId",
                        columns: x => new { x.OperadorId, x.VersionId },
                        principalTable: "VersionesTarifario",
                        principalColumns: new[] { "OperadorId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TarifasZonaModalidad_ZonasCobertura_OperadorId_ZonaId",
                        columns: x => new { x.OperadorId, x.ZonaId },
                        principalTable: "ZonasCobertura",
                        principalColumns: new[] { "OperadorId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AjustesTarifario",
                columns: table => new
                {
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ZonaId = table.Column<Guid>(type: "uuid", nullable: false),
                    Modalidad = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Nombre = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Tipo = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    ImporteFijoUyu = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: true),
                    Porcentaje = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AjustesTarifario", x => new { x.OperadorId, x.Id });
                    table.CheckConstraint("CK_AjustesTarifario_Name", "length(btrim(\"Nombre\")) > 0");
                    table.CheckConstraint("CK_AjustesTarifario_Type", "\"Tipo\" IN ('RecargoFijo', 'RecargoPorcentual', 'BonificacionFija', 'BonificacionPorcentual')");
                    table.CheckConstraint("CK_AjustesTarifario_Value", "((\"Tipo\" IN ('RecargoFijo', 'BonificacionFija') AND \"ImporteFijoUyu\" IS NOT NULL AND \"Porcentaje\" IS NULL AND \"ImporteFijoUyu\" >= 0) OR (\"Tipo\" IN ('RecargoPorcentual', 'BonificacionPorcentual') AND \"ImporteFijoUyu\" IS NULL AND \"Porcentaje\" BETWEEN 0 AND 100))");
                    table.ForeignKey(
                        name: "FK_AjustesTarifario_TarifasZonaModalidad_OperadorId_VersionId_~",
                        columns: x => new { x.OperadorId, x.VersionId, x.ZonaId, x.Modalidad },
                        principalTable: "TarifasZonaModalidad",
                        principalColumns: new[] { "OperadorId", "VersionId", "ZonaId", "Modalidad" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AjustesTarifario_OperadorId_VersionId_ZonaId_Modalidad",
                table: "AjustesTarifario",
                columns: new[] { "OperadorId", "VersionId", "ZonaId", "Modalidad" });

            migrationBuilder.CreateIndex(
                name: "IX_TarifariosOperador_OperadorId_VersionVigenteId",
                table: "TarifariosOperador",
                columns: new[] { "OperadorId", "VersionVigenteId" });

            migrationBuilder.CreateIndex(
                name: "IX_TarifasZonaModalidad_OperadorId_ZonaId",
                table: "TarifasZonaModalidad",
                columns: new[] { "OperadorId", "ZonaId" });

            migrationBuilder.CreateIndex(
                name: "IX_VersionesTarifario_CreadaPorUsuarioId",
                table: "VersionesTarifario",
                column: "CreadaPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_VersionesTarifario_PublicadaPorUsuarioId",
                table: "VersionesTarifario",
                column: "PublicadaPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "UX_VersionesTarifario_OneDraftPerOperator",
                table: "VersionesTarifario",
                column: "OperadorId",
                unique: true,
                filter: "\"Estado\" = 'Borrador'");

            migrationBuilder.CreateIndex(
                name: "UX_VersionesTarifario_PublicationNumber",
                table: "VersionesTarifario",
                columns: new[] { "OperadorId", "NumeroVersionPublicada" },
                unique: true,
                filter: "\"NumeroVersionPublicada\" IS NOT NULL");

            migrationBuilder.Sql("""
                ALTER TABLE public."TarifariosOperador" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."VersionesTarifario" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."TarifasZonaModalidad" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."AjustesTarifario" OWNER TO nexoruta_schema_owner;

                ALTER TABLE public."TarifariosOperador" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."TarifariosOperador" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."VersionesTarifario" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."VersionesTarifario" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."TarifasZonaModalidad" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."TarifasZonaModalidad" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."AjustesTarifario" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."AjustesTarifario" FORCE ROW LEVEL SECURITY;

                REVOKE ALL ON public."TarifariosOperador", public."VersionesTarifario", public."TarifasZonaModalidad",
                    public."AjustesTarifario" FROM PUBLIC, nexoruta_api, nexoruta_worker, nexoruta_bootstrap;
                GRANT SELECT, INSERT, UPDATE ON public."TarifariosOperador", public."VersionesTarifario" TO nexoruta_api;
                GRANT SELECT, INSERT, UPDATE, DELETE ON public."TarifasZonaModalidad", public."AjustesTarifario" TO nexoruta_api;

                CREATE POLICY tariff_root_operator_select ON public."TarifariosOperador" FOR SELECT TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY tariff_root_operator_insert ON public."TarifariosOperador" FOR INSERT TO nexoruta_api
                    WITH CHECK (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY tariff_root_operator_update ON public."TarifariosOperador" FOR UPDATE TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'))
                    WITH CHECK (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY tariff_root_guard_owner_select ON public."TarifariosOperador" FOR SELECT TO nexoruta_schema_owner USING (true);

                CREATE POLICY tariff_version_operator_select ON public."VersionesTarifario" FOR SELECT TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY tariff_version_operator_insert ON public."VersionesTarifario" FOR INSERT TO nexoruta_api
                    WITH CHECK (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id')
                        AND "CreadaPorUsuarioId" = nexoruta_app.ctx_uuid('user_id'));
                CREATE POLICY tariff_version_operator_update ON public."VersionesTarifario" FOR UPDATE TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'))
                    WITH CHECK (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id')
                        AND ("PublicadaPorUsuarioId" IS NULL OR "PublicadaPorUsuarioId" = nexoruta_app.ctx_uuid('user_id')));

                CREATE POLICY tariff_rate_operator_select ON public."TarifasZonaModalidad" FOR SELECT TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY tariff_rate_operator_insert ON public."TarifasZonaModalidad" FOR INSERT TO nexoruta_api
                    WITH CHECK (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY tariff_rate_operator_update ON public."TarifasZonaModalidad" FOR UPDATE TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'))
                    WITH CHECK (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY tariff_rate_operator_delete ON public."TarifasZonaModalidad" FOR DELETE TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));

                CREATE POLICY tariff_adjustment_operator_select ON public."AjustesTarifario" FOR SELECT TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY tariff_adjustment_operator_insert ON public."AjustesTarifario" FOR INSERT TO nexoruta_api
                    WITH CHECK (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY tariff_adjustment_operator_update ON public."AjustesTarifario" FOR UPDATE TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'))
                    WITH CHECK (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY tariff_adjustment_operator_delete ON public."AjustesTarifario" FOR DELETE TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));

                -- Trigger owner needs SELECT on the parent under FORCE RLS; this policy is limited to a NOLOGIN schema-owner role.
                CREATE POLICY tariff_guard_owner_select ON public."VersionesTarifario" FOR SELECT TO nexoruta_schema_owner USING (true);

                CREATE FUNCTION nexoruta_app.guard_tariff_version()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog
                AS $function$
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        IF NEW."Estado" <> 'Borrador' OR NEW."VersionConcurrencia" <> 0
                           OR NEW."NumeroVersionPublicada" IS NOT NULL OR NEW."PublicadaUtc" IS NOT NULL
                           OR NEW."PublicadaPorUsuarioId" IS NOT NULL THEN
                            RAISE EXCEPTION 'Tariff versions must start as a draft' USING ERRCODE = '55000';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF TG_OP = 'DELETE' THEN
                        IF OLD."Estado" = 'Publicada' THEN
                            RAISE EXCEPTION 'Published tariff versions are immutable' USING ERRCODE = '55000';
                        END IF;
                        RETURN OLD;
                    END IF;
                    IF OLD."Estado" = 'Publicada' THEN
                        RAISE EXCEPTION 'Published tariff versions are immutable' USING ERRCODE = '55000';
                    END IF;
                    IF NEW."OperadorId" <> OLD."OperadorId" OR NEW."Id" <> OLD."Id"
                       OR NEW."Moneda" <> OLD."Moneda" OR NEW."CreadaUtc" <> OLD."CreadaUtc"
                       OR NEW."CreadaPorUsuarioId" <> OLD."CreadaPorUsuarioId" THEN
                        RAISE EXCEPTION 'Tariff version identity is immutable' USING ERRCODE = '55000';
                    END IF;
                    IF NEW."Estado" = 'Borrador' THEN
                        IF NEW."VersionConcurrencia" <> OLD."VersionConcurrencia" + 1
                           OR NEW."NumeroVersionPublicada" IS NOT NULL OR NEW."PublicadaUtc" IS NOT NULL
                           OR NEW."PublicadaPorUsuarioId" IS NOT NULL THEN
                            RAISE EXCEPTION 'Draft edits must advance exactly one version' USING ERRCODE = '55000';
                        END IF;
                    ELSIF NEW."Estado" = 'Publicada' THEN
                        IF NEW."VersionConcurrencia" <> OLD."VersionConcurrencia"
                           OR NEW."NumeroVersionPublicada" IS NULL OR NEW."NumeroVersionPublicada" <= 0
                           OR NEW."PublicadaUtc" IS NULL OR NEW."PublicadaPorUsuarioId" IS NULL THEN
                            RAISE EXCEPTION 'Publication metadata is invalid' USING ERRCODE = '55000';
                        END IF;
                    ELSE
                        RAISE EXCEPTION 'Unknown tariff version state' USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END
                $function$;
                ALTER FUNCTION nexoruta_app.guard_tariff_version() OWNER TO nexoruta_schema_owner;
                REVOKE ALL ON FUNCTION nexoruta_app.guard_tariff_version() FROM PUBLIC, nexoruta_api, nexoruta_worker, nexoruta_bootstrap;
                CREATE TRIGGER tariff_version_guard BEFORE INSERT OR UPDATE OR DELETE ON public."VersionesTarifario"
                    FOR EACH ROW EXECUTE FUNCTION nexoruta_app.guard_tariff_version();

                CREATE FUNCTION nexoruta_app.guard_tariff_child_immutability()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public
                AS $function$
                DECLARE
                    row_operator uuid;
                    row_version uuid;
                    parent_state text;
                BEGIN
                    IF TG_OP = 'UPDATE' OR TG_OP = 'DELETE' THEN
                        SELECT "Estado" INTO parent_state FROM public."VersionesTarifario"
                         WHERE "OperadorId" = OLD."OperadorId" AND "Id" = OLD."VersionId";
                        IF parent_state = 'Publicada' THEN
                            RAISE EXCEPTION 'Published tariff children are immutable' USING ERRCODE = '55000';
                        END IF;
                    END IF;
                    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
                    SELECT "Estado" INTO parent_state FROM public."VersionesTarifario"
                     WHERE "OperadorId" = NEW."OperadorId" AND "Id" = NEW."VersionId";
                    IF parent_state IS DISTINCT FROM 'Borrador' THEN
                        RAISE EXCEPTION 'Tariff children can only be written in a draft' USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END
                $function$;
                ALTER FUNCTION nexoruta_app.guard_tariff_child_immutability() OWNER TO nexoruta_schema_owner;
                REVOKE ALL ON FUNCTION nexoruta_app.guard_tariff_child_immutability() FROM PUBLIC, nexoruta_api, nexoruta_worker, nexoruta_bootstrap;
                CREATE TRIGGER tariff_rate_immutability BEFORE INSERT OR UPDATE OR DELETE ON public."TarifasZonaModalidad"
                    FOR EACH ROW EXECUTE FUNCTION nexoruta_app.guard_tariff_child_immutability();
                CREATE TRIGGER tariff_adjustment_immutability BEFORE INSERT OR UPDATE OR DELETE ON public."AjustesTarifario"
                    FOR EACH ROW EXECUTE FUNCTION nexoruta_app.guard_tariff_child_immutability();

                CREATE FUNCTION nexoruta_app.guard_current_tariff_pointer()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog
                AS $function$
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        IF NEW."VersionVigenteId" IS NOT NULL OR NEW."UltimoNumeroPublicacion" <> 0 THEN
                            RAISE EXCEPTION 'A tariff root must start without a publication' USING ERRCODE = '55000';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF NEW."OperadorId" <> OLD."OperadorId" THEN
                        RAISE EXCEPTION 'Tariff root identity is immutable' USING ERRCODE = '55000';
                    END IF;
                    IF NEW."VersionVigenteId" IS DISTINCT FROM OLD."VersionVigenteId" THEN
                        IF NEW."VersionVigenteId" IS NULL OR NEW."UltimoNumeroPublicacion" <> OLD."UltimoNumeroPublicacion" + 1 THEN
                            RAISE EXCEPTION 'Tariff publication number must advance atomically' USING ERRCODE = '55000';
                        END IF;
                    ELSIF NEW."UltimoNumeroPublicacion" <> OLD."UltimoNumeroPublicacion" THEN
                        RAISE EXCEPTION 'Tariff publication counter requires a new current version' USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END
                $function$;
                ALTER FUNCTION nexoruta_app.guard_current_tariff_pointer() OWNER TO nexoruta_schema_owner;
                REVOKE ALL ON FUNCTION nexoruta_app.guard_current_tariff_pointer() FROM PUBLIC, nexoruta_api, nexoruta_worker, nexoruta_bootstrap;
                CREATE TRIGGER tariff_root_guard BEFORE INSERT OR UPDATE ON public."TarifariosOperador"
                    FOR EACH ROW EXECUTE FUNCTION nexoruta_app.guard_current_tariff_pointer();

                CREATE FUNCTION nexoruta_app.assert_current_tariff_pointer()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public
                AS $function$
                DECLARE
                    current_state text;
                    current_number integer;
                BEGIN
                    IF NEW."VersionVigenteId" IS NULL THEN RETURN NEW; END IF;
                    SELECT "Estado", "NumeroVersionPublicada" INTO current_state, current_number
                      FROM public."VersionesTarifario"
                     WHERE "OperadorId" = NEW."OperadorId" AND "Id" = NEW."VersionVigenteId";
                    IF current_state IS DISTINCT FROM 'Publicada' OR current_number IS DISTINCT FROM NEW."UltimoNumeroPublicacion" THEN
                        RAISE EXCEPTION 'Current tariff pointer must reference its published version' USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END
                $function$;
                ALTER FUNCTION nexoruta_app.assert_current_tariff_pointer() OWNER TO nexoruta_schema_owner;
                REVOKE ALL ON FUNCTION nexoruta_app.assert_current_tariff_pointer() FROM PUBLIC, nexoruta_api, nexoruta_worker, nexoruta_bootstrap;
                CREATE CONSTRAINT TRIGGER tariff_root_current_publication
                    AFTER INSERT OR UPDATE ON public."TarifariosOperador" DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION nexoruta_app.assert_current_tariff_pointer();

                CREATE FUNCTION nexoruta_app.assert_published_tariff_is_current()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public
                AS $function$
                DECLARE
                    pointer_id uuid;
                    pointer_number integer;
                BEGIN
                    IF OLD."Estado" = 'Borrador' AND NEW."Estado" = 'Publicada' THEN
                        SELECT "VersionVigenteId", "UltimoNumeroPublicacion" INTO pointer_id, pointer_number
                          FROM public."TarifariosOperador" WHERE "OperadorId" = NEW."OperadorId";
                        IF pointer_id IS DISTINCT FROM NEW."Id" OR pointer_number IS DISTINCT FROM NEW."NumeroVersionPublicada" THEN
                            RAISE EXCEPTION 'Published tariff version must become current atomically' USING ERRCODE = '55000';
                        END IF;
                    END IF;
                    RETURN NEW;
                END
                $function$;
                ALTER FUNCTION nexoruta_app.assert_published_tariff_is_current() OWNER TO nexoruta_schema_owner;
                REVOKE ALL ON FUNCTION nexoruta_app.assert_published_tariff_is_current() FROM PUBLIC, nexoruta_api, nexoruta_worker, nexoruta_bootstrap;
                CREATE CONSTRAINT TRIGGER tariff_version_current_publication
                    AFTER UPDATE ON public."VersionesTarifario" DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION nexoruta_app.assert_published_tariff_is_current();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS tariff_version_current_publication ON public."VersionesTarifario";
                DROP TRIGGER IF EXISTS tariff_root_current_publication ON public."TarifariosOperador";
                DROP TRIGGER IF EXISTS tariff_root_guard ON public."TarifariosOperador";
                DROP TRIGGER IF EXISTS tariff_adjustment_immutability ON public."AjustesTarifario";
                DROP TRIGGER IF EXISTS tariff_rate_immutability ON public."TarifasZonaModalidad";
                DROP TRIGGER IF EXISTS tariff_version_guard ON public."VersionesTarifario";
                DROP FUNCTION IF EXISTS nexoruta_app.assert_published_tariff_is_current();
                DROP FUNCTION IF EXISTS nexoruta_app.assert_current_tariff_pointer();
                DROP FUNCTION IF EXISTS nexoruta_app.guard_current_tariff_pointer();
                DROP FUNCTION IF EXISTS nexoruta_app.guard_tariff_child_immutability();
                DROP FUNCTION IF EXISTS nexoruta_app.guard_tariff_version();
                """);
            migrationBuilder.DropTable(
                name: "AjustesTarifario");

            migrationBuilder.DropTable(
                name: "TarifariosOperador");

            migrationBuilder.DropTable(
                name: "TarifasZonaModalidad");

            migrationBuilder.DropTable(
                name: "VersionesTarifario");
        }
    }
}
