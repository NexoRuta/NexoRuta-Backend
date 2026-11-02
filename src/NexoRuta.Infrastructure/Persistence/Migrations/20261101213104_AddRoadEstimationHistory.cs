using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRoadEstimationHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EstimacionesRuta",
                columns: table => new
                {
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    RutaId = table.Column<Guid>(type: "uuid", nullable: false),
                    NumeroVersion = table.Column<long>(type: "bigint", nullable: false),
                    ComandoId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionRuta = table.Column<long>(type: "bigint", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    RegistradaEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ContextoHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Entrada = table.Column<string>(type: "jsonb", nullable: false),
                    Resultado = table.Column<string>(type: "jsonb", nullable: false),
                    Motivo = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EstimacionesRuta", x => new { x.OperadorId, x.RutaId, x.NumeroVersion });
                    table.ForeignKey(
                        name: "FK_EstimacionesRuta_HojasRuta_OperadorId_RutaId",
                        columns: x => new { x.OperadorId, x.RutaId },
                        principalTable: "HojasRuta",
                        principalColumns: new[] { "OperadorId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PerfilesVialesVehiculo",
                columns: table => new
                {
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    VehiculoId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    Compatibilidad = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConfirmadoEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Motivo = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PerfilesVialesVehiculo", x => new { x.OperadorId, x.VehiculoId });
                    table.CheckConstraint("CK_PerfilVial_Compatibilidad", "\"Compatibilidad\" IN ('SinDeterminar','DriveConfirmado','NoCompatible') AND \"Version\">0");
                    table.ForeignKey(
                        name: "FK_PerfilesVialesVehiculo_Vehiculos_OperadorId_VehiculoId",
                        columns: x => new { x.OperadorId, x.VehiculoId },
                        principalTable: "Vehiculos",
                        principalColumns: new[] { "OperadorId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EstimacionesRuta_OperadorId_ComandoId",
                table: "EstimacionesRuta",
                columns: new[] { "OperadorId", "ComandoId" },
                unique: true);
            migrationBuilder.Sql("""
                ALTER TABLE public."PerfilesVialesVehiculo" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."EstimacionesRuta" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."PerfilesVialesVehiculo" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."PerfilesVialesVehiculo" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."EstimacionesRuta" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."EstimacionesRuta" FORCE ROW LEVEL SECURITY;
                REVOKE ALL ON public."PerfilesVialesVehiculo",public."EstimacionesRuta" FROM PUBLIC,nexoruta_api,nexoruta_worker;
                GRANT SELECT,INSERT,UPDATE ON public."PerfilesVialesVehiculo" TO nexoruta_api;
                GRANT SELECT,INSERT ON public."EstimacionesRuta" TO nexoruta_api;
                CREATE POLICY perfil_read ON public."PerfilesVialesVehiculo" FOR SELECT TO nexoruta_api USING(nexoruta_app.setting('actor_kind')='operador' AND "OperadorId"=nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY perfil_write ON public."PerfilesVialesVehiculo" FOR ALL TO nexoruta_api USING(nexoruta_app.setting('actor_kind')='operador' AND "OperadorId"=nexoruta_app.ctx_uuid('org_id')) WITH CHECK(nexoruta_app.setting('actor_kind')='operador' AND "OperadorId"=nexoruta_app.ctx_uuid('org_id') AND "ActorId"=nexoruta_app.ctx_uuid('access_id'));
                CREATE POLICY estimation_read ON public."EstimacionesRuta" FOR SELECT TO nexoruta_api USING(nexoruta_app.setting('actor_kind')='operador' AND "OperadorId"=nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY estimation_insert ON public."EstimacionesRuta" FOR INSERT TO nexoruta_api WITH CHECK(nexoruta_app.setting('actor_kind')='operador' AND "OperadorId"=nexoruta_app.ctx_uuid('org_id') AND "ActorId"=nexoruta_app.ctx_uuid('access_id'));
                CREATE FUNCTION public.guard_road_estimation_history() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$ BEGIN RAISE EXCEPTION 'Road estimation history is immutable.' USING ERRCODE='42501';END $$;
                ALTER FUNCTION public.guard_road_estimation_history() OWNER TO nexoruta_schema_owner;
                CREATE TRIGGER road_history_immutable BEFORE UPDATE OR DELETE ON public."EstimacionesRuta" FOR EACH ROW EXECUTE FUNCTION public.guard_road_estimation_history();
                CREATE FUNCTION public.guard_road_profile() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$ BEGIN
                  IF NEW."OperadorId" IS DISTINCT FROM OLD."OperadorId" OR NEW."VehiculoId" IS DISTINCT FROM OLD."VehiculoId" OR NEW."Version"<>OLD."Version"+1 THEN RAISE EXCEPTION 'Road profile update requires exact identity and next version.' USING ERRCODE='42501'; END IF;RETURN NEW;END $$;
                ALTER FUNCTION public.guard_road_profile() OWNER TO nexoruta_schema_owner;
                CREATE TRIGGER road_profile_version BEFORE UPDATE ON public."PerfilesVialesVehiculo" FOR EACH ROW EXECUTE FUNCTION public.guard_road_profile();
                CREATE TABLE public."UsoRoutingDiario" ("OperadorId" uuid NOT NULL,"Fecha" date NOT NULL,"Solicitudes" integer NOT NULL CHECK("Solicitudes">=0),"DisponibleEnUtc" timestamptz NOT NULL,PRIMARY KEY("OperadorId","Fecha"));
                ALTER TABLE public."UsoRoutingDiario" OWNER TO nexoruta_schema_owner;
                ALTER TABLE public."UsoRoutingDiario" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."UsoRoutingDiario" FORCE ROW LEVEL SECURITY;
                REVOKE ALL ON public."UsoRoutingDiario" FROM PUBLIC,nexoruta_api,nexoruta_worker;
                CREATE POLICY budget_owner ON public."UsoRoutingDiario" TO nexoruta_schema_owner USING(true) WITH CHECK(true);
                CREATE FUNCTION nexoruta_app.reserve_routing_request(op uuid,global_max integer,operator_max integer,rps integer) RETURNS double precision LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
                DECLARE day date:=(clock_timestamp() AT TIME ZONE 'UTC')::date;zero uuid:='00000000-0000-0000-0000-000000000000';g integer;o integer;slot timestamptz;
                BEGIN
                  IF nexoruta_app.setting('actor_kind')<>'operador' OR op IS DISTINCT FROM nexoruta_app.ctx_uuid('org_id') OR op=zero OR global_max<1 OR operator_max<1 OR rps<1 OR rps>5 THEN RAISE EXCEPTION 'Invalid routing budget scope.' USING ERRCODE='42501'; END IF;
                  PERFORM pg_advisory_xact_lock(hashtextextended('nexoruta-routing-'||day::text,0));
                  INSERT INTO public."UsoRoutingDiario" VALUES(zero,day,0,clock_timestamp()),(op,day,0,clock_timestamp()) ON CONFLICT DO NOTHING;
                  SELECT "Solicitudes",GREATEST("DisponibleEnUtc",clock_timestamp()) INTO g,slot FROM public."UsoRoutingDiario" WHERE "OperadorId"=zero AND "Fecha"=day;
                  SELECT "Solicitudes" INTO o FROM public."UsoRoutingDiario" WHERE "OperadorId"=op AND "Fecha"=day;
                  IF g>=global_max OR o>=operator_max OR slot>=((day+1)::timestamp AT TIME ZONE 'UTC') THEN RETURN NULL;END IF;
                  UPDATE public."UsoRoutingDiario" SET "Solicitudes"="Solicitudes"+1,"DisponibleEnUtc"=CASE WHEN "OperadorId"=zero THEN slot+make_interval(secs=>1.0/rps) ELSE slot END WHERE "Fecha"=day AND "OperadorId" IN(zero,op);
                  RETURN GREATEST(0,extract(epoch FROM slot-clock_timestamp()))::double precision;
                END $$;
                ALTER FUNCTION nexoruta_app.reserve_routing_request(uuid,integer,integer,integer) OWNER TO nexoruta_schema_owner;
                REVOKE ALL ON FUNCTION nexoruta_app.reserve_routing_request(uuid,integer,integer,integer) FROM PUBLIC,nexoruta_worker;
                GRANT EXECUTE ON FUNCTION nexoruta_app.reserve_routing_request(uuid,integer,integer,integer) TO nexoruta_api;
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN IF EXISTS(SELECT 1 FROM public."EstimacionesRuta") OR EXISTS(SELECT 1 FROM public."PerfilesVialesVehiculo") OR EXISTS(SELECT 1 FROM public."UsoRoutingDiario") THEN RAISE EXCEPTION 'B4 history/profile/budget data exists; deliberate preserving rollback required.';END IF;END $$;
                DROP FUNCTION nexoruta_app.reserve_routing_request(uuid,integer,integer,integer);
                DROP TABLE public."UsoRoutingDiario";
                DROP TRIGGER road_history_immutable ON public."EstimacionesRuta";
                DROP TRIGGER road_profile_version ON public."PerfilesVialesVehiculo";
                DROP FUNCTION public.guard_road_estimation_history();DROP FUNCTION public.guard_road_profile();
                """);

            migrationBuilder.DropTable(
                name: "EstimacionesRuta");

            migrationBuilder.DropTable(
                name: "PerfilesVialesVehiculo");
        }
    }
}
