using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace NexoRuta.Infrastructure.Persistence.Migrations;
[DbContext(typeof(NexoRutaDbContext))]
[Migration("20261102030000_ApplyShipmentTariffs")]
public sealed class ApplyShipmentTariffs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        GRANT SELECT ON public."TarifariosOperador",public."VersionesTarifario",public."TarifasZonaModalidad",public."AjustesTarifario",public."ZonasCobertura" TO nexoruta_shipment_fn;
        CREATE POLICY admission_tariff_root ON public."TarifariosOperador" FOR SELECT TO nexoruta_shipment_fn
            USING (nexoruta_app.setting('actor_kind')='comercio' AND "OperadorId"=nexoruta_app.ctx_uuid('selected_operator_id'));
        CREATE POLICY admission_tariff_version ON public."VersionesTarifario" FOR SELECT TO nexoruta_shipment_fn
            USING (nexoruta_app.setting('actor_kind')='comercio' AND "OperadorId"=nexoruta_app.ctx_uuid('selected_operator_id') AND "Estado"='Publicada');
        CREATE POLICY admission_tariff_rate ON public."TarifasZonaModalidad" FOR SELECT TO nexoruta_shipment_fn
            USING (nexoruta_app.setting('actor_kind')='comercio' AND "OperadorId"=nexoruta_app.ctx_uuid('selected_operator_id'));
        CREATE POLICY admission_tariff_adjustment ON public."AjustesTarifario" FOR SELECT TO nexoruta_shipment_fn
            USING (nexoruta_app.setting('actor_kind')='comercio' AND "OperadorId"=nexoruta_app.ctx_uuid('selected_operator_id'));
        CREATE POLICY admission_zone_read ON public."ZonasCobertura" FOR SELECT TO nexoruta_shipment_fn
            USING (nexoruta_app.setting('actor_kind')='comercio' AND "OperadorId"=nexoruta_app.ctx_uuid('selected_operator_id'));
        GRANT UPDATE ("VersionVigenteId") ON public."TarifariosOperador" TO nexoruta_shipment_fn;
        GRANT UPDATE ("Version") ON public."ZonasCobertura" TO nexoruta_shipment_fn;
        CREATE POLICY admission_tariff_lock ON public."TarifariosOperador" FOR UPDATE TO nexoruta_shipment_fn
            USING (nexoruta_app.setting('actor_kind')='comercio' AND "OperadorId"=nexoruta_app.ctx_uuid('selected_operator_id')) WITH CHECK(false);
        CREATE POLICY admission_zone_lock ON public."ZonasCobertura" FOR UPDATE TO nexoruta_shipment_fn
            USING (nexoruta_app.setting('actor_kind')='comercio' AND "OperadorId"=nexoruta_app.ctx_uuid('selected_operator_id')) WITH CHECK(false);
        CREATE FUNCTION nexoruta_app.shipment_service_offers(p_operator uuid,p_lat double precision,p_lon double precision,p_version uuid DEFAULT NULL)
            RETURNS text LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,nexoruta_app AS $$
        DECLARE publication uuid; result text;
        BEGIN
            IF p_lat IS NULL OR p_lon IS NULL OR NOT p_lat BETWEEN -90 AND 90 OR NOT p_lon BETWEEN -180 AND 180 THEN
                RAISE EXCEPTION 'Invalid destination coordinates.' USING ERRCODE='22023'; END IF;
            PERFORM nexoruta_app.shipment_rules_for_admission(p_operator);
            SELECT "VersionVigenteId" INTO publication FROM public."TarifariosOperador" WHERE "OperadorId"=p_operator FOR SHARE;
            publication := coalesce(p_version,publication);
            PERFORM 1 FROM public."ZonasCobertura" z WHERE z."OperadorId"=p_operator AND z."Estado"='Activa'
                AND public.st_covers(z."Geometria",public.st_setsrid(public.st_makepoint(p_lon,p_lat),4326)) FOR SHARE;
            SELECT coalesce(jsonb_agg(jsonb_build_object('ZonaNombre',z."Nombre",'VersionZona',z."Version",'RevisionZona',z."RevisionActual",'Tarifa',jsonb_build_object(
                'ZonaId',t."ZonaId",'Modalidad',t."Modalidad",'ImporteBaseUyu',t."ImporteBaseUyu",
                'PrecioPorKilogramoUyu',t."PrecioPorKilogramoUyu",'PrecioPorMetroCubicoUyu',t."PrecioPorMetroCubicoUyu",
                'VersionTarifarioId',v."Id",'NumeroVersionPublicada',v."NumeroVersionPublicada",'VersionConcurrencia',v."VersionConcurrencia",'Moneda',v."Moneda",
                'Ajustes',coalesce((SELECT jsonb_agg(jsonb_build_object('Nombre',a."Nombre",'Tipo',a."Tipo",'Valor',coalesce(a."ImporteFijoUyu",a."Porcentaje")) ORDER BY a."Id")
                    FROM public."AjustesTarifario" a WHERE a."OperadorId"=t."OperadorId" AND a."VersionId"=t."VersionId" AND a."ZonaId"=t."ZonaId" AND a."Modalidad"=t."Modalidad"),'[]'::jsonb))) ORDER BY z."Id",t."Modalidad"),'[]'::jsonb)::text
                INTO result FROM public."TarifasZonaModalidad" t
                JOIN public."VersionesTarifario" v ON v."OperadorId"=t."OperadorId" AND v."Id"=t."VersionId" AND v."Estado"='Publicada'
                JOIN public."ZonasCobertura" z ON z."OperadorId"=t."OperadorId" AND z."Id"=t."ZonaId"
                WHERE t."OperadorId"=p_operator AND t."VersionId"=publication AND z."Estado"='Activa'
                    AND public.st_covers(z."Geometria",public.st_setsrid(public.st_makepoint(p_lon,p_lat),4326));
            RETURN result;
        END $$;
        ALTER FUNCTION nexoruta_app.shipment_service_offers(uuid,double precision,double precision,uuid) OWNER TO nexoruta_shipment_fn;
        REVOKE ALL ON FUNCTION nexoruta_app.shipment_service_offers(uuid,double precision,double precision,uuid) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION nexoruta_app.shipment_service_offers(uuid,double precision,double precision,uuid) TO nexoruta_api;
        CREATE TABLE public."TarifasEnvio" (
            "Id" uuid PRIMARY KEY,"OperadorId" uuid NOT NULL,"EnvioId" uuid NOT NULL,"VersionTarifarioId" uuid NOT NULL,
            "Servicio" jsonb NOT NULL,"Resultado" jsonb NOT NULL,"Cobertura" jsonb NOT NULL,"CreadoEnUtc" timestamptz NOT NULL DEFAULT clock_timestamp(),
            FOREIGN KEY ("OperadorId","EnvioId") REFERENCES public."Envios"("OperadorId","Id"),
            FOREIGN KEY ("OperadorId","VersionTarifarioId") REFERENCES public."VersionesTarifario"("OperadorId","Id"));
        CREATE FUNCTION nexoruta_app.guard_shipment_tariff() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
        BEGIN RAISE EXCEPTION 'Applied shipment tariffs are immutable.' USING ERRCODE='55000'; END $$;
        ALTER FUNCTION nexoruta_app.guard_shipment_tariff() OWNER TO nexoruta_schema_owner;
        REVOKE ALL ON FUNCTION nexoruta_app.guard_shipment_tariff() FROM PUBLIC;
        CREATE TRIGGER immutable_shipment_tariff BEFORE UPDATE OR DELETE ON public."TarifasEnvio"
            FOR EACH ROW EXECUTE FUNCTION nexoruta_app.guard_shipment_tariff();
        CREATE INDEX ON public."TarifasEnvio" ("OperadorId","EnvioId","CreadoEnUtc");
        ALTER TABLE public."TarifasEnvio" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE public."TarifasEnvio" FORCE ROW LEVEL SECURITY;
        GRANT SELECT,INSERT ON public."TarifasEnvio" TO nexoruta_api;
        CREATE POLICY shipment_tariff_read ON public."TarifasEnvio" FOR SELECT TO nexoruta_api
            USING (EXISTS (SELECT 1 FROM public."Envios" e WHERE e."OperadorId"="TarifasEnvio"."OperadorId" AND e."Id"="TarifasEnvio"."EnvioId"));
        CREATE POLICY shipment_tariff_insert ON public."TarifasEnvio" FOR INSERT TO nexoruta_api
            WITH CHECK (nexoruta_app.setting('actor_kind')='comercio' AND EXISTS (SELECT 1 FROM public."Envios" e
                WHERE e."OperadorId"="TarifasEnvio"."OperadorId" AND e."Id"="TarifasEnvio"."EnvioId" AND e."ComercioId"=nexoruta_app.ctx_uuid('org_id')));
                CREATE OR REPLACE FUNCTION nexoruta_app.bind_import_identity(
                    p_reference text, p_shipment uuid, p_operator uuid, p_fingerprint_version smallint, p_fingerprint bytea)
                RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
                AS $bind_import$
                DECLARE v_commerce_id uuid := nexoruta_app.ctx_uuid('org_id');
                        v_user_id uuid := nexoruta_app.ctx_uuid('user_id');
                BEGIN
                    IF nexoruta_app.setting('actor_kind') IS DISTINCT FROM 'comercio' OR v_commerce_id IS NULL
                        OR v_user_id IS NULL OR nexoruta_app.ctx_uuid('access_id') IS NULL
                        OR nexoruta_app.ctx_uuid('shipment_id') IS DISTINCT FROM p_shipment
                        OR nexoruta_app.ctx_uuid('selected_operator_id') IS DISTINCT FROM p_operator
                        OR NOT EXISTS (SELECT 1 FROM public."AccesosUsuario" a WHERE a."Id" = nexoruta_app.ctx_uuid('access_id')
                            AND a."UsuarioId" = v_user_id AND a."ComercioId" = v_commerce_id AND a."Activo" AND a."RolId" IS NOT NULL)
                        OR NOT EXISTS (SELECT 1 FROM public."Comercios" c WHERE c."Id" = v_commerce_id AND c."Estado" = 'Activa') THEN
                        RAISE EXCEPTION 'Import identity binding is not authorized.' USING ERRCODE = '42501';
                    END IF;
                    IF p_reference IS NULL OR char_length(p_reference) NOT BETWEEN 1 AND 128
                        OR p_reference ~ '[[:cntrl:]]' OR p_fingerprint_version <> 2
                        OR p_fingerprint IS NULL OR octet_length(p_fingerprint) <> 32 THEN
                        RAISE EXCEPTION 'Import identity values are invalid.' USING ERRCODE = '22023';
                    END IF;
                    INSERT INTO public."IdentidadesEnvioImportado"
                        ("ComercioId", "ReferenciaExterna", "EnvioId", "OperadorId", "VersionHuella", "Huella")
                    VALUES (v_commerce_id, p_reference, p_shipment, p_operator, p_fingerprint_version, p_fingerprint);
                END
                $bind_import$;
                ALTER FUNCTION nexoruta_app.bind_import_identity(text, uuid, uuid, smallint, bytea) OWNER TO nexoruta_shipment_fn;
                REVOKE ALL ON FUNCTION nexoruta_app.bind_import_identity(text, uuid, uuid, smallint, bytea) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION nexoruta_app.bind_import_identity(text, uuid, uuid, smallint, bytea) TO nexoruta_api;
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP TABLE public."TarifasEnvio";
        DROP FUNCTION nexoruta_app.guard_shipment_tariff();
        DROP FUNCTION nexoruta_app.shipment_service_offers(uuid,double precision,double precision,uuid);
        DROP POLICY admission_zone_lock ON public."ZonasCobertura";
        DROP POLICY admission_tariff_lock ON public."TarifariosOperador";
        DROP POLICY admission_zone_read ON public."ZonasCobertura";
        DROP POLICY admission_tariff_adjustment ON public."AjustesTarifario";
        DROP POLICY admission_tariff_rate ON public."TarifasZonaModalidad";
        DROP POLICY admission_tariff_version ON public."VersionesTarifario";
        DROP POLICY admission_tariff_root ON public."TarifariosOperador";
        REVOKE SELECT ON public."TarifariosOperador",public."VersionesTarifario",public."TarifasZonaModalidad",public."AjustesTarifario",public."ZonasCobertura" FROM nexoruta_shipment_fn;
        REVOKE UPDATE ("VersionVigenteId") ON public."TarifariosOperador" FROM nexoruta_shipment_fn;
        REVOKE UPDATE ("Version") ON public."ZonasCobertura" FROM nexoruta_shipment_fn;
                CREATE OR REPLACE FUNCTION nexoruta_app.bind_import_identity(
                    p_reference text, p_shipment uuid, p_operator uuid, p_fingerprint_version smallint, p_fingerprint bytea)
                RETURNS void LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
                AS $bind_import$
                DECLARE v_commerce_id uuid := nexoruta_app.ctx_uuid('org_id');
                        v_user_id uuid := nexoruta_app.ctx_uuid('user_id');
                BEGIN
                    IF nexoruta_app.setting('actor_kind') IS DISTINCT FROM 'comercio' OR v_commerce_id IS NULL
                        OR v_user_id IS NULL OR nexoruta_app.ctx_uuid('access_id') IS NULL
                        OR nexoruta_app.ctx_uuid('shipment_id') IS DISTINCT FROM p_shipment
                        OR nexoruta_app.ctx_uuid('selected_operator_id') IS DISTINCT FROM p_operator
                        OR NOT EXISTS (SELECT 1 FROM public."AccesosUsuario" a WHERE a."Id" = nexoruta_app.ctx_uuid('access_id')
                            AND a."UsuarioId" = v_user_id AND a."ComercioId" = v_commerce_id AND a."Activo" AND a."RolId" IS NOT NULL)
                        OR NOT EXISTS (SELECT 1 FROM public."Comercios" c WHERE c."Id" = v_commerce_id AND c."Estado" = 'Activa') THEN
                        RAISE EXCEPTION 'Import identity binding is not authorized.' USING ERRCODE = '42501';
                    END IF;
                    IF p_reference IS NULL OR char_length(p_reference) NOT BETWEEN 1 AND 128
                        OR p_reference ~ '[[:cntrl:]]' OR p_fingerprint_version <> 1
                        OR p_fingerprint IS NULL OR octet_length(p_fingerprint) <> 32 THEN
                        RAISE EXCEPTION 'Import identity values are invalid.' USING ERRCODE = '22023';
                    END IF;
                    INSERT INTO public."IdentidadesEnvioImportado"
                        ("ComercioId", "ReferenciaExterna", "EnvioId", "OperadorId", "VersionHuella", "Huella")
                    VALUES (v_commerce_id, p_reference, p_shipment, p_operator, p_fingerprint_version, p_fingerprint);
                END
                $bind_import$;
                ALTER FUNCTION nexoruta_app.bind_import_identity(text, uuid, uuid, smallint, bytea) OWNER TO nexoruta_shipment_fn;
                REVOKE ALL ON FUNCTION nexoruta_app.bind_import_identity(text, uuid, uuid, smallint, bytea) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION nexoruta_app.bind_import_identity(text, uuid, uuid, smallint, bytea) TO nexoruta_api;
        """);
}
