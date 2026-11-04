using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace NexoRuta.Infrastructure.Persistence.Migrations;
[DbContext(typeof(NexoRutaDbContext))]
[Migration("20261103000000_AddDepotParcelReceipts")]
public sealed class AddDepotParcelReceipts : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
        CREATE TABLE public."RecepcionesBulto" (
            "ComandoId" uuid NOT NULL, "OperadorId" uuid NOT NULL, "EnvioId" uuid NOT NULL,
            "BultoId" uuid NOT NULL REFERENCES public."Bultos"("Id"),
            "UsuarioId" uuid NOT NULL REFERENCES public."Usuarios"("Id"),
            "AccesoId" uuid NOT NULL REFERENCES public."AccesosUsuario"("Id"),
            "Secuencia" bigint GENERATED ALWAYS AS IDENTITY,
            PRIMARY KEY ("OperadorId","ComandoId"),
            "RegistradaEnUtc" timestamptz NOT NULL, "Declarado" jsonb NOT NULL, "Cotejo" jsonb NOT NULL,
            FOREIGN KEY ("OperadorId","EnvioId") REFERENCES public."Envios"("OperadorId","Id"),
            CHECK (jsonb_typeof("Declarado")='object' AND jsonb_typeof("Cotejo")='object'));
        CREATE INDEX ON public."RecepcionesBulto" ("OperadorId","EnvioId","RegistradaEnUtc","ComandoId");
        ALTER TABLE public."RecepcionesBulto" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE public."RecepcionesBulto" FORCE ROW LEVEL SECURITY;
        GRANT SELECT, INSERT ON public."RecepcionesBulto" TO nexoruta_api;
        GRANT USAGE ON SEQUENCE public."RecepcionesBulto_Secuencia_seq" TO nexoruta_api;
        CREATE POLICY receipt_read ON public."RecepcionesBulto" FOR SELECT TO nexoruta_api
            USING (nexoruta_app.setting('actor_kind')='operador' AND "OperadorId"=nexoruta_app.ctx_uuid('org_id'));
        CREATE POLICY receipt_insert ON public."RecepcionesBulto" FOR INSERT TO nexoruta_api
            WITH CHECK (nexoruta_app.setting('actor_kind')='operador' AND "OperadorId"=nexoruta_app.ctx_uuid('org_id')
                AND "UsuarioId"=nexoruta_app.ctx_uuid('user_id') AND "AccesoId"=nexoruta_app.ctx_uuid('access_id')
                AND EXISTS (SELECT 1 FROM public."Bultos" b WHERE b."Id"="BultoId"
                    AND b."EnvioId"="RecepcionesBulto"."EnvioId" AND b."OperadorId"="RecepcionesBulto"."OperadorId"));
        CREATE FUNCTION nexoruta_app.guard_depot_receipt() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
        BEGIN RAISE EXCEPTION 'Depot receipt history is immutable.' USING ERRCODE='55000'; END $$;
        ALTER FUNCTION nexoruta_app.guard_depot_receipt() OWNER TO nexoruta_schema_owner;
        REVOKE ALL ON FUNCTION nexoruta_app.guard_depot_receipt() FROM PUBLIC;
        CREATE TRIGGER receipt_no_change BEFORE UPDATE OR DELETE ON public."RecepcionesBulto"
            FOR EACH ROW EXECUTE FUNCTION nexoruta_app.guard_depot_receipt();
        CREATE TRIGGER receipt_no_truncate BEFORE TRUNCATE ON public."RecepcionesBulto"
            EXECUTE FUNCTION nexoruta_app.guard_depot_receipt();
        """);
    protected override void Down(MigrationBuilder m) => m.Sql("""
        DROP TABLE public."RecepcionesBulto";
        DROP FUNCTION nexoruta_app.guard_depot_receipt();
        """);
}
