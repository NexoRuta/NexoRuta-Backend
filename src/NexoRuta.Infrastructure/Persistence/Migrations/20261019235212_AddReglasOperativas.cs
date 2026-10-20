using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReglasOperativas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VersionesReglasOperativas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Estado = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    NumeroVersionPublicada = table.Column<int>(type: "integer", nullable: true),
                    VersionConcurrencia = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    MaxIntentosEntrega = table.Column<int>(type: "integer", nullable: true),
                    IntervaloEntreIntentosMinutos = table.Column<int>(type: "integer", nullable: true),
                    PoliticaDevolucionAlAgotarIntentos = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    PlazoEstandarMinutos = table.Column<int>(type: "integer", nullable: true),
                    PlazoUrgenteMinutos = table.Column<int>(type: "integer", nullable: true),
                    CreadaUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreadaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    PublicadaUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PublicadaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VersionesReglasOperativas", x => new { x.OperadorId, x.Id });
                    table.CheckConstraint("CK_VersionesReglasOperativas_Concurrency", "\"VersionConcurrencia\" >= 0");
                    table.CheckConstraint("CK_VersionesReglasOperativas_Interval", "\"IntervaloEntreIntentosMinutos\" IS NULL OR \"IntervaloEntreIntentosMinutos\" BETWEEN 1 AND 525600");
                    table.CheckConstraint("CK_VersionesReglasOperativas_MaxAttempts", "\"MaxIntentosEntrega\" IS NULL OR \"MaxIntentosEntrega\" BETWEEN 1 AND 100");
                    table.CheckConstraint("CK_VersionesReglasOperativas_Publication", "(\"Estado\" = 'Borrador' AND \"NumeroVersionPublicada\" IS NULL AND \"PublicadaUtc\" IS NULL AND \"PublicadaPorUsuarioId\" IS NULL) OR (\"Estado\" = 'Publicada' AND \"NumeroVersionPublicada\" > 0 AND \"PublicadaUtc\" IS NOT NULL AND \"PublicadaPorUsuarioId\" IS NOT NULL)");
                    table.CheckConstraint("CK_VersionesReglasOperativas_ReturnPolicy", "\"PoliticaDevolucionAlAgotarIntentos\" IS NULL OR \"PoliticaDevolucionAlAgotarIntentos\" IN ('DecisionOperativa', 'IniciarAutomaticamente')");
                    table.CheckConstraint("CK_VersionesReglasOperativas_StandardDeadline", "\"PlazoEstandarMinutos\" IS NULL OR \"PlazoEstandarMinutos\" BETWEEN 1 AND 525600");
                    table.CheckConstraint("CK_VersionesReglasOperativas_State", "\"Estado\" IN ('Borrador', 'Publicada')");
                    table.CheckConstraint("CK_VersionesReglasOperativas_UrgentDeadline", "\"PlazoUrgenteMinutos\" IS NULL OR \"PlazoUrgenteMinutos\" BETWEEN 1 AND 525600");
                    table.ForeignKey(
                        name: "FK_VersionesReglasOperativas_Operadores_OperadorId",
                        column: x => x.OperadorId,
                        principalTable: "Operadores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VersionesReglasOperativas_Usuarios_CreadaPorUsuarioId",
                        column: x => x.CreadaPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VersionesReglasOperativas_Usuarios_PublicadaPorUsuarioId",
                        column: x => x.PublicadaPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MotivosNoEntregaVersion",
                columns: table => new
                {
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Codigo = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Descripcion = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Disponible = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MotivosNoEntregaVersion", x => new { x.OperadorId, x.VersionId, x.Codigo });
                    table.CheckConstraint("CK_MotivosNoEntregaVersion_Code", "\"Codigo\" ~ '^[A-Z][A-Z0-9_]{0,39}$'");
                    table.CheckConstraint("CK_MotivosNoEntregaVersion_Description", "length(btrim(\"Descripcion\")) BETWEEN 1 AND 120 AND \"Descripcion\" !~ '[[:cntrl:]]'");
                    table.ForeignKey(
                        name: "FK_MotivosNoEntregaVersion_VersionesReglasOperativas_OperadorI~",
                        columns: x => new { x.OperadorId, x.VersionId },
                        principalTable: "VersionesReglasOperativas",
                        principalColumns: new[] { "OperadorId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReglasOperativasOperador",
                columns: table => new
                {
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionVigenteId = table.Column<Guid>(type: "uuid", nullable: true),
                    UltimoNumeroPublicacion = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    ActualizadoUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReglasOperativasOperador", x => x.OperadorId);
                    table.CheckConstraint("CK_ReglasOperativasOperador_PublicationCounter", "\"UltimoNumeroPublicacion\" >= 0");
                    table.ForeignKey(
                        name: "FK_ReglasOperativasOperador_Operadores_OperadorId",
                        column: x => x.OperadorId,
                        principalTable: "Operadores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReglasOperativasOperador_VersionesReglasOperativas_Operador~",
                        columns: x => new { x.OperadorId, x.VersionVigenteId },
                        principalTable: "VersionesReglasOperativas",
                        principalColumns: new[] { "OperadorId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RequisitosPruebaEntrega",
                columns: table => new
                {
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Tipo = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequisitosPruebaEntrega", x => new { x.OperadorId, x.VersionId, x.Tipo });
                    table.CheckConstraint("CK_RequisitosPruebaEntrega_Type", "\"Tipo\" IN ('FirmaEnPantalla', 'Fotografia', 'NombreReceptor', 'DocumentoReceptor')");
                    table.ForeignKey(
                        name: "FK_RequisitosPruebaEntrega_VersionesReglasOperativas_OperadorI~",
                        columns: x => new { x.OperadorId, x.VersionId },
                        principalTable: "VersionesReglasOperativas",
                        principalColumns: new[] { "OperadorId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReglasOperativasOperador_OperadorId_VersionVigenteId",
                table: "ReglasOperativasOperador",
                columns: new[] { "OperadorId", "VersionVigenteId" });

            migrationBuilder.CreateIndex(
                name: "IX_VersionesReglasOperativas_CreadaPorUsuarioId",
                table: "VersionesReglasOperativas",
                column: "CreadaPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_VersionesReglasOperativas_PublicadaPorUsuarioId",
                table: "VersionesReglasOperativas",
                column: "PublicadaPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "UX_VersionesReglasOperativas_OneDraftPerOperator",
                table: "VersionesReglasOperativas",
                column: "OperadorId",
                unique: true,
                filter: "\"Estado\" = 'Borrador'");

            migrationBuilder.CreateIndex(
                name: "UX_VersionesReglasOperativas_PublicationNumber",
                table: "VersionesReglasOperativas",
                columns: new[] { "OperadorId", "NumeroVersionPublicada" },
                unique: true,
                filter: "\"NumeroVersionPublicada\" IS NOT NULL");

            migrationBuilder.Sql("""
                ALTER TABLE public."ReglasOperativasOperador" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."VersionesReglasOperativas" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."RequisitosPruebaEntrega" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."MotivosNoEntregaVersion" OWNER TO nexoruta_schema_owner;

                ALTER TABLE public."ReglasOperativasOperador" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."ReglasOperativasOperador" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."VersionesReglasOperativas" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."VersionesReglasOperativas" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."RequisitosPruebaEntrega" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."RequisitosPruebaEntrega" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."MotivosNoEntregaVersion" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."MotivosNoEntregaVersion" FORCE ROW LEVEL SECURITY;

                REVOKE ALL ON public."ReglasOperativasOperador", public."VersionesReglasOperativas",
                    public."RequisitosPruebaEntrega", public."MotivosNoEntregaVersion"
                    FROM PUBLIC, nexoruta_api, nexoruta_worker, nexoruta_bootstrap;
                GRANT SELECT, INSERT, UPDATE ON public."ReglasOperativasOperador",
                    public."VersionesReglasOperativas" TO nexoruta_api;
                GRANT SELECT, INSERT, DELETE ON public."RequisitosPruebaEntrega" TO nexoruta_api;
                GRANT SELECT, INSERT, UPDATE ON public."MotivosNoEntregaVersion" TO nexoruta_api;

                CREATE POLICY operational_rules_root_operator_select ON public."ReglasOperativasOperador"
                    FOR SELECT TO nexoruta_api USING
                    (nexoruta_app.setting('actor_kind') = 'operador'
                     AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY operational_rules_root_operator_insert ON public."ReglasOperativasOperador"
                    FOR INSERT TO nexoruta_api WITH CHECK
                    (nexoruta_app.setting('actor_kind') = 'operador'
                     AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY operational_rules_root_operator_update ON public."ReglasOperativasOperador"
                    FOR UPDATE TO nexoruta_api USING
                    (nexoruta_app.setting('actor_kind') = 'operador'
                     AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'))
                    WITH CHECK
                    (nexoruta_app.setting('actor_kind') = 'operador'
                     AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY operational_rules_root_guard_owner_select ON public."ReglasOperativasOperador"
                    FOR SELECT TO nexoruta_schema_owner USING (true);

                CREATE POLICY operational_rules_version_operator_select ON public."VersionesReglasOperativas"
                    FOR SELECT TO nexoruta_api USING
                    (nexoruta_app.setting('actor_kind') = 'operador'
                     AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY operational_rules_version_operator_insert ON public."VersionesReglasOperativas"
                    FOR INSERT TO nexoruta_api WITH CHECK
                    (nexoruta_app.setting('actor_kind') = 'operador'
                     AND "OperadorId" = nexoruta_app.ctx_uuid('org_id')
                     AND "CreadaPorUsuarioId" = nexoruta_app.ctx_uuid('user_id'));
                CREATE POLICY operational_rules_version_operator_update ON public."VersionesReglasOperativas"
                    FOR UPDATE TO nexoruta_api USING
                    (nexoruta_app.setting('actor_kind') = 'operador'
                     AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'))
                    WITH CHECK
                    (nexoruta_app.setting('actor_kind') = 'operador'
                     AND "OperadorId" = nexoruta_app.ctx_uuid('org_id')
                     AND ("PublicadaPorUsuarioId" IS NULL
                          OR "PublicadaPorUsuarioId" = nexoruta_app.ctx_uuid('user_id')));
                CREATE POLICY operational_rules_version_guard_owner_select ON public."VersionesReglasOperativas"
                    FOR SELECT TO nexoruta_schema_owner USING (true);

                CREATE POLICY operational_rules_evidence_operator_select ON public."RequisitosPruebaEntrega"
                    FOR SELECT TO nexoruta_api USING
                    (nexoruta_app.setting('actor_kind') = 'operador'
                     AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY operational_rules_evidence_operator_insert ON public."RequisitosPruebaEntrega"
                    FOR INSERT TO nexoruta_api WITH CHECK
                    (nexoruta_app.setting('actor_kind') = 'operador'
                     AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY operational_rules_evidence_operator_delete ON public."RequisitosPruebaEntrega"
                    FOR DELETE TO nexoruta_api USING
                    (nexoruta_app.setting('actor_kind') = 'operador'
                     AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY operational_rules_evidence_guard_owner_select ON public."RequisitosPruebaEntrega"
                    FOR SELECT TO nexoruta_schema_owner USING (true);

                CREATE POLICY operational_rules_motive_operator_select ON public."MotivosNoEntregaVersion"
                    FOR SELECT TO nexoruta_api USING
                    (nexoruta_app.setting('actor_kind') = 'operador'
                     AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY operational_rules_motive_operator_insert ON public."MotivosNoEntregaVersion"
                    FOR INSERT TO nexoruta_api WITH CHECK
                    (nexoruta_app.setting('actor_kind') = 'operador'
                     AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY operational_rules_motive_operator_update ON public."MotivosNoEntregaVersion"
                    FOR UPDATE TO nexoruta_api USING
                    (nexoruta_app.setting('actor_kind') = 'operador'
                     AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'))
                    WITH CHECK
                    (nexoruta_app.setting('actor_kind') = 'operador'
                     AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY operational_rules_motive_operator_delete ON public."MotivosNoEntregaVersion"
                    FOR DELETE TO nexoruta_api USING
                    (nexoruta_app.setting('actor_kind') = 'operador'
                     AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY operational_rules_motive_guard_owner_select ON public."MotivosNoEntregaVersion"
                    FOR SELECT TO nexoruta_schema_owner USING (true);

                CREATE FUNCTION nexoruta_app.guard_operational_rules_version()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public
                AS $function$
                DECLARE
                    requirement_count integer;
                    available_motive_count integer;
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        IF NEW."Estado" <> 'Borrador' OR NEW."VersionConcurrencia" <> 0
                           OR NEW."NumeroVersionPublicada" IS NOT NULL OR NEW."PublicadaUtc" IS NOT NULL
                           OR NEW."PublicadaPorUsuarioId" IS NOT NULL THEN
                            RAISE EXCEPTION 'Operational rules versions must start as drafts' USING ERRCODE = '55000';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF TG_OP = 'DELETE' THEN
                        IF OLD."Estado" = 'Publicada' THEN
                            RAISE EXCEPTION 'Published operational rules are immutable' USING ERRCODE = '55000';
                        END IF;
                        RETURN OLD;
                    END IF;
                    IF OLD."Estado" = 'Publicada' THEN
                        RAISE EXCEPTION 'Published operational rules are immutable' USING ERRCODE = '55000';
                    END IF;
                    IF NEW."OperadorId" <> OLD."OperadorId" OR NEW."Id" <> OLD."Id"
                       OR NEW."CreadaUtc" <> OLD."CreadaUtc"
                       OR NEW."CreadaPorUsuarioId" <> OLD."CreadaPorUsuarioId" THEN
                        RAISE EXCEPTION 'Operational rules version identity is immutable' USING ERRCODE = '55000';
                    END IF;
                    IF NEW."Estado" = 'Borrador' THEN
                        IF NEW."VersionConcurrencia" <> OLD."VersionConcurrencia" + 1
                           OR NEW."NumeroVersionPublicada" IS NOT NULL OR NEW."PublicadaUtc" IS NOT NULL
                           OR NEW."PublicadaPorUsuarioId" IS NOT NULL THEN
                            RAISE EXCEPTION 'Draft edits must advance exactly one version' USING ERRCODE = '55000';
                        END IF;
                    ELSIF NEW."Estado" = 'Publicada' THEN
                        IF NEW."VersionConcurrencia" <> OLD."VersionConcurrencia"
                           OR NEW."NumeroVersionPublicada" IS NULL OR NEW."PublicadaUtc" IS NULL
                           OR NEW."PublicadaPorUsuarioId" IS NULL THEN
                            RAISE EXCEPTION 'Publication metadata is invalid' USING ERRCODE = '55000';
                        END IF;
                        SELECT count(*) INTO requirement_count FROM public."RequisitosPruebaEntrega"
                         WHERE "OperadorId" = NEW."OperadorId" AND "VersionId" = NEW."Id";
                        SELECT count(*) INTO available_motive_count FROM public."MotivosNoEntregaVersion"
                         WHERE "OperadorId" = NEW."OperadorId" AND "VersionId" = NEW."Id" AND "Disponible";
                        IF NEW."MaxIntentosEntrega" IS NULL OR NEW."IntervaloEntreIntentosMinutos" IS NULL
                           OR NEW."PoliticaDevolucionAlAgotarIntentos" IS NULL
                           OR NEW."PlazoEstandarMinutos" IS NULL OR NEW."PlazoUrgenteMinutos" IS NULL
                           OR requirement_count = 0 OR available_motive_count = 0 THEN
                            RAISE EXCEPTION 'Operational rules publication is incomplete' USING ERRCODE = '23514';
                        END IF;
                    ELSE
                        RAISE EXCEPTION 'Unknown operational rules state' USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END
                $function$;
                ALTER FUNCTION nexoruta_app.guard_operational_rules_version() OWNER TO nexoruta_schema_owner;
                REVOKE ALL ON FUNCTION nexoruta_app.guard_operational_rules_version() FROM PUBLIC, nexoruta_api, nexoruta_worker, nexoruta_bootstrap;
                CREATE TRIGGER operational_rules_version_guard BEFORE INSERT OR UPDATE OR DELETE ON public."VersionesReglasOperativas"
                    FOR EACH ROW EXECUTE FUNCTION nexoruta_app.guard_operational_rules_version();

                CREATE FUNCTION nexoruta_app.guard_operational_rules_children()
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
                CREATE TRIGGER operational_rules_evidence_immutability BEFORE INSERT OR UPDATE OR DELETE ON public."RequisitosPruebaEntrega"
                    FOR EACH ROW EXECUTE FUNCTION nexoruta_app.guard_operational_rules_children();
                CREATE TRIGGER operational_rules_motive_immutability BEFORE INSERT OR UPDATE OR DELETE ON public."MotivosNoEntregaVersion"
                    FOR EACH ROW EXECUTE FUNCTION nexoruta_app.guard_operational_rules_children();

                CREATE FUNCTION nexoruta_app.guard_operational_rules_root()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public
                AS $function$
                DECLARE
                    publication_state text;
                    publication_number integer;
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        IF NEW."VersionVigenteId" IS NOT NULL OR NEW."UltimoNumeroPublicacion" <> 0 THEN
                            RAISE EXCEPTION 'Operational rules root must start without a publication' USING ERRCODE = '55000';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'Operational rules root cannot be deleted' USING ERRCODE = '55000';
                    END IF;
                    IF NEW."OperadorId" <> OLD."OperadorId"
                       OR NEW."UltimoNumeroPublicacion" < OLD."UltimoNumeroPublicacion" THEN
                        RAISE EXCEPTION 'Operational rules root identity is immutable' USING ERRCODE = '55000';
                    END IF;
                    IF NEW."VersionVigenteId" IS NOT DISTINCT FROM OLD."VersionVigenteId" THEN
                        IF NEW."UltimoNumeroPublicacion" <> OLD."UltimoNumeroPublicacion" THEN
                            RAISE EXCEPTION 'Publication counter and current version must advance together' USING ERRCODE = '55000';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF NEW."VersionVigenteId" IS NULL
                       OR NEW."UltimoNumeroPublicacion" <> OLD."UltimoNumeroPublicacion" + 1 THEN
                        RAISE EXCEPTION 'Current publication must advance exactly one version' USING ERRCODE = '55000';
                    END IF;
                    SELECT "Estado", "NumeroVersionPublicada" INTO publication_state, publication_number
                      FROM public."VersionesReglasOperativas"
                     WHERE "OperadorId" = NEW."OperadorId" AND "Id" = NEW."VersionVigenteId";
                    IF publication_state IS DISTINCT FROM 'Publicada'
                       OR publication_number IS DISTINCT FROM NEW."UltimoNumeroPublicacion" THEN
                        RAISE EXCEPTION 'Current version must reference the new publication' USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END
                $function$;
                ALTER FUNCTION nexoruta_app.guard_operational_rules_root() OWNER TO nexoruta_schema_owner;
                REVOKE ALL ON FUNCTION nexoruta_app.guard_operational_rules_root() FROM PUBLIC, nexoruta_api, nexoruta_worker, nexoruta_bootstrap;
                CREATE TRIGGER operational_rules_root_guard BEFORE INSERT OR UPDATE OR DELETE ON public."ReglasOperativasOperador"
                    FOR EACH ROW EXECUTE FUNCTION nexoruta_app.guard_operational_rules_root();

                CREATE FUNCTION nexoruta_app.assert_current_operational_rules_publication()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public
                AS $function$
                DECLARE
                    current_id uuid;
                    current_number integer;
                BEGIN
                    IF NEW."Estado" <> 'Publicada' THEN RETURN NULL; END IF;
                    SELECT "VersionVigenteId", "UltimoNumeroPublicacion" INTO current_id, current_number
                      FROM public."ReglasOperativasOperador" WHERE "OperadorId" = NEW."OperadorId";
                    IF current_id IS DISTINCT FROM NEW."Id"
                       OR current_number IS DISTINCT FROM NEW."NumeroVersionPublicada" THEN
                        RAISE EXCEPTION 'Published rules must be the current operator publication' USING ERRCODE = '55000';
                    END IF;
                    RETURN NULL;
                END
                $function$;
                ALTER FUNCTION nexoruta_app.assert_current_operational_rules_publication() OWNER TO nexoruta_schema_owner;
                REVOKE ALL ON FUNCTION nexoruta_app.assert_current_operational_rules_publication() FROM PUBLIC, nexoruta_api, nexoruta_worker, nexoruta_bootstrap;
                CREATE CONSTRAINT TRIGGER operational_rules_current_publication_check
                    AFTER UPDATE ON public."VersionesReglasOperativas" DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION nexoruta_app.assert_current_operational_rules_publication();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS operational_rules_current_publication_check ON public."VersionesReglasOperativas";
                DROP TRIGGER IF EXISTS operational_rules_root_guard ON public."ReglasOperativasOperador";
                DROP TRIGGER IF EXISTS operational_rules_motive_immutability ON public."MotivosNoEntregaVersion";
                DROP TRIGGER IF EXISTS operational_rules_evidence_immutability ON public."RequisitosPruebaEntrega";
                DROP TRIGGER IF EXISTS operational_rules_version_guard ON public."VersionesReglasOperativas";
                DROP FUNCTION IF EXISTS nexoruta_app.assert_current_operational_rules_publication();
                DROP FUNCTION IF EXISTS nexoruta_app.guard_operational_rules_root();
                DROP FUNCTION IF EXISTS nexoruta_app.guard_operational_rules_children();
                DROP FUNCTION IF EXISTS nexoruta_app.guard_operational_rules_version();
                """);
            migrationBuilder.DropTable(
                name: "MotivosNoEntregaVersion");

            migrationBuilder.DropTable(
                name: "ReglasOperativasOperador");

            migrationBuilder.DropTable(
                name: "RequisitosPruebaEntrega");

            migrationBuilder.DropTable(
                name: "VersionesReglasOperativas");
        }
    }
}
