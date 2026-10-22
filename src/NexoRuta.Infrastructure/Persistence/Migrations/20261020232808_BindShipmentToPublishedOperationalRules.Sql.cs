using Microsoft.EntityFrameworkCore.Migrations;
namespace NexoRuta.Infrastructure.Persistence.Migrations;
public partial class BindShipmentToPublishedOperationalRules
{
    private static void ConfigurarAdmision(MigrationBuilder migration) => migration.Sql("""

GRANT SELECT ON public."ReglasOperativasOperador",public."VersionesReglasOperativas" TO nexoruta_shipment_fn;
GRANT UPDATE ("VersionVigenteId") ON public."ReglasOperativasOperador" TO nexoruta_shipment_fn;
CREATE POLICY shipment_rules_root_read ON public."ReglasOperativasOperador" FOR SELECT TO nexoruta_shipment_fn
    USING (nexoruta_app.setting('actor_kind')='comercio' AND "OperadorId"=nexoruta_app.ctx_uuid('selected_operator_id'));
CREATE POLICY shipment_rules_root_lock ON public."ReglasOperativasOperador" FOR UPDATE TO nexoruta_shipment_fn
    USING (nexoruta_app.setting('actor_kind')='comercio' AND "OperadorId"=nexoruta_app.ctx_uuid('selected_operator_id')) WITH CHECK(false);
CREATE POLICY shipment_rules_version_read ON public."VersionesReglasOperativas" FOR SELECT TO nexoruta_shipment_fn
    USING ("OperadorId"=nexoruta_app.ctx_uuid('selected_operator_id') AND "Estado"='Publicada'
        AND EXISTS(SELECT 1 FROM public."ReglasOperativasOperador" r WHERE r."OperadorId"="VersionesReglasOperativas"."OperadorId"
            AND r."VersionVigenteId"="VersionesReglasOperativas"."Id"));
CREATE FUNCTION nexoruta_app.shipment_rules_for_admission(p_operator uuid) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,nexoruta_app AS $$
DECLARE publication uuid;
BEGIN
    IF nexoruta_app.setting('actor_kind') IS DISTINCT FROM 'comercio' OR NOT EXISTS (
        SELECT 1 FROM public."AccesosUsuario" a WHERE a."Id"=nexoruta_app.ctx_uuid('access_id')
            AND a."UsuarioId"=nexoruta_app.ctx_uuid('user_id') AND a."ComercioId"=nexoruta_app.ctx_uuid('org_id')
            AND a."Activo" AND a."RolId" IS NOT NULL)
       OR NOT EXISTS(SELECT 1 FROM public."Comercios" c WHERE c."Id"=nexoruta_app.ctx_uuid('org_id') AND c."Estado"='Activa') THEN
        RAISE EXCEPTION 'Shipment rules lookup is not authorized.' USING ERRCODE='42501';
    END IF;
    PERFORM set_config('nexoruta.selected_operator_id',p_operator::text,true);
    IF NOT EXISTS(SELECT 1 FROM public."Operadores" o WHERE o."Id"=p_operator AND o."Estado"='Activa') THEN
        RAISE EXCEPTION 'Selected operator is unavailable.' USING ERRCODE='NXR01';
    END IF;
    SELECT v."Id" INTO publication FROM public."ReglasOperativasOperador" r
        JOIN public."VersionesReglasOperativas" v ON v."OperadorId"=r."OperadorId" AND v."Id"=r."VersionVigenteId"
        WHERE r."OperadorId"=p_operator AND v."Estado"='Publicada';
    RETURN publication;
END $$;
ALTER FUNCTION nexoruta_app.shipment_rules_for_admission(uuid) OWNER TO nexoruta_shipment_fn;
REVOKE ALL ON FUNCTION nexoruta_app.shipment_rules_for_admission(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nexoruta_app.shipment_rules_for_admission(uuid) TO nexoruta_api;
CREATE FUNCTION nexoruta_app.guard_shipment_rules_reference() RETURNS trigger
LANGUAGE plpgsql SET search_path=pg_catalog AS $$
BEGIN
    IF NEW."VersionReglasId" IS DISTINCT FROM OLD."VersionReglasId" THEN
        RAISE EXCEPTION 'Shipment operational rules reference is immutable.' USING ERRCODE='55000';
    END IF;
    RETURN NEW;
END $$;
ALTER FUNCTION nexoruta_app.guard_shipment_rules_reference() OWNER TO nexoruta_schema_owner;
REVOKE ALL ON FUNCTION nexoruta_app.guard_shipment_rules_reference() FROM PUBLIC;
CREATE TRIGGER shipment_rules_reference_immutable BEFORE UPDATE ON public."Envios"
    FOR EACH ROW EXECUTE FUNCTION nexoruta_app.guard_shipment_rules_reference();
DROP FUNCTION nexoruta_app.create_shipment(uuid, uuid, uuid, uuid, text, text, text, text, numeric, numeric, numeric, numeric);
CREATE OR REPLACE FUNCTION nexoruta_app.create_shipment(
                    p_shipment_id uuid, p_recipient_id uuid, p_address_id uuid, p_parcel_id uuid,
                    p_recipient_name text, p_recipient_email text, p_address text, p_parcel_code text,
                    p_weight numeric, p_length numeric, p_width numeric, p_height numeric, p_rules_version uuid)
                RETURNS uuid
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
                AS $create_shipment$
                DECLARE v_commerce_id uuid := nexoruta_app.ctx_uuid('org_id');
                        v_operator_id uuid := nexoruta_app.ctx_uuid('selected_operator_id');
                        v_user_id uuid := nexoruta_app.ctx_uuid('user_id');
                BEGIN
                    IF p_recipient_email IS NULL OR length(p_recipient_email) > 254
                       OR p_recipient_email <> btrim(p_recipient_email)
                       OR p_recipient_email !~ '^[^[:space:]@]+@[^[:space:]@]+$' THEN
                        RAISE EXCEPTION 'A valid recipient email is required.' USING ERRCODE = '22023';
                    END IF;
                    IF nexoruta_app.setting('actor_kind') <> 'comercio' OR v_commerce_id IS NULL
                       OR v_operator_id IS NULL OR v_user_id IS NULL OR p_shipment_id IS NULL
                       OR p_recipient_id IS NULL OR p_address_id IS NULL OR p_parcel_id IS NULL
                       OR p_shipment_id <> nexoruta_app.ctx_uuid('shipment_id')
                       OR NOT EXISTS (SELECT 1 FROM public."Comercios" c WHERE c."Id" = v_commerce_id AND c."Estado" = 'Activa')
                       OR NOT EXISTS (SELECT 1 FROM public."AccesosUsuario" a WHERE a."UsuarioId" = v_user_id
                           AND a."ComercioId" = v_commerce_id AND a."Activo" AND a."RolId" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Shipment context is not authorized.' USING ERRCODE = '42501';
                    END IF;

                    -- Lock only the selected operator. This serializes shipment creation with platform
                    -- suspension while keeping commerce from locking or reading inactive operators.
                    PERFORM 1 FROM public."Operadores" o
                     WHERE o."Id" = v_operator_id AND o."Estado" = 'Activa'
                     FOR UPDATE;
                    IF NOT FOUND THEN
                        RAISE EXCEPTION 'Selected operator is unavailable.' USING ERRCODE = 'NXR01';
                    END IF;

                    IF p_rules_version IS NULL OR NOT EXISTS (
                        SELECT 1 FROM public."ReglasOperativasOperador" r
                        JOIN public."VersionesReglasOperativas" v ON v."OperadorId"=r."OperadorId" AND v."Id"=r."VersionVigenteId"
                        WHERE r."OperadorId"=v_operator_id AND v."Id"=p_rules_version AND v."Estado"='Publicada'
                        FOR SHARE OF r) THEN
                        RAISE EXCEPTION 'Published operational rules changed or are missing.' USING ERRCODE='NXR03';
                    END IF;
                    PERFORM set_config('nexoruta.recipient_id', p_recipient_id::text, true);
                    PERFORM set_config('nexoruta.address_id', p_address_id::text, true);
                    PERFORM set_config('nexoruta.parcel_id', p_parcel_id::text, true);

                    INSERT INTO public."Destinatarios" ("Id", "OperadorId", "Nombre", "Email")
                    VALUES (p_recipient_id, v_operator_id, p_recipient_name, p_recipient_email);
                    INSERT INTO public."Direcciones" ("Id", "OperadorId", "Descripcion")
                    VALUES (p_address_id, v_operator_id, p_address);
                    INSERT INTO public."Envios" ("Id", "OperadorId", "ComercioId", "CreadoPorUsuarioId",
                        "DestinatarioId", "DireccionId", "VersionReglasId", "Estado")
                    VALUES (p_shipment_id, v_operator_id, v_commerce_id, v_user_id,
                        p_recipient_id, p_address_id, p_rules_version, 'Admitido');
                    INSERT INTO public."Bultos" ("Id", "OperadorId", "EnvioId", "Codigo", "PesoGramos",
                        "LargoCentimetros", "AnchoCentimetros", "AltoCentimetros")
                    VALUES (p_parcel_id, v_operator_id, p_shipment_id, p_parcel_code, p_weight,
                        p_length, p_width, p_height);
                    RETURN p_shipment_id;
                END
                $create_shipment$;
ALTER FUNCTION nexoruta_app.create_shipment(uuid, uuid, uuid, uuid, text, text, text, text, numeric, numeric, numeric, numeric, uuid) OWNER TO nexoruta_shipment_fn;
REVOKE ALL ON FUNCTION nexoruta_app.create_shipment(uuid, uuid, uuid, uuid, text, text, text, text, numeric, numeric, numeric, numeric, uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nexoruta_app.create_shipment(uuid, uuid, uuid, uuid, text, text, text, text, numeric, numeric, numeric, numeric, uuid) TO nexoruta_api;
""");
    private static void RetirarAdmision(MigrationBuilder migration) => migration.Sql("""

DROP TRIGGER shipment_rules_reference_immutable ON public."Envios";
DROP FUNCTION nexoruta_app.guard_shipment_rules_reference();
DROP FUNCTION nexoruta_app.shipment_rules_for_admission(uuid);
DROP POLICY shipment_rules_version_read ON public."VersionesReglasOperativas";
DROP POLICY shipment_rules_root_read ON public."ReglasOperativasOperador";
DROP POLICY shipment_rules_root_lock ON public."ReglasOperativasOperador";
REVOKE SELECT ON public."ReglasOperativasOperador",public."VersionesReglasOperativas" FROM nexoruta_shipment_fn;
REVOKE UPDATE ("VersionVigenteId") ON public."ReglasOperativasOperador" FROM nexoruta_shipment_fn;
DROP FUNCTION nexoruta_app.create_shipment(uuid, uuid, uuid, uuid, text, text, text, text, numeric, numeric, numeric, numeric, uuid);
CREATE OR REPLACE FUNCTION nexoruta_app.create_shipment(
                    p_shipment_id uuid, p_recipient_id uuid, p_address_id uuid, p_parcel_id uuid,
                    p_recipient_name text, p_recipient_email text, p_address text, p_parcel_code text,
                    p_weight numeric, p_length numeric, p_width numeric, p_height numeric)
                RETURNS uuid
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
                AS $create_shipment$
                DECLARE v_commerce_id uuid := nexoruta_app.ctx_uuid('org_id');
                        v_operator_id uuid := nexoruta_app.ctx_uuid('selected_operator_id');
                        v_user_id uuid := nexoruta_app.ctx_uuid('user_id');
                BEGIN
                    IF p_recipient_email IS NULL OR length(p_recipient_email) > 254
                       OR p_recipient_email <> btrim(p_recipient_email)
                       OR p_recipient_email !~ '^[^[:space:]@]+@[^[:space:]@]+$' THEN
                        RAISE EXCEPTION 'A valid recipient email is required.' USING ERRCODE = '22023';
                    END IF;
                    IF nexoruta_app.setting('actor_kind') <> 'comercio' OR v_commerce_id IS NULL
                       OR v_operator_id IS NULL OR v_user_id IS NULL OR p_shipment_id IS NULL
                       OR p_recipient_id IS NULL OR p_address_id IS NULL OR p_parcel_id IS NULL
                       OR p_shipment_id <> nexoruta_app.ctx_uuid('shipment_id')
                       OR NOT EXISTS (SELECT 1 FROM public."Comercios" c WHERE c."Id" = v_commerce_id AND c."Estado" = 'Activa')
                       OR NOT EXISTS (SELECT 1 FROM public."AccesosUsuario" a WHERE a."UsuarioId" = v_user_id
                           AND a."ComercioId" = v_commerce_id AND a."Activo" AND a."RolId" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Shipment context is not authorized.' USING ERRCODE = '42501';
                    END IF;

                    -- Lock only the selected operator. This serializes shipment creation with platform
                    -- suspension while keeping commerce from locking or reading inactive operators.
                    PERFORM 1 FROM public."Operadores" o
                     WHERE o."Id" = v_operator_id AND o."Estado" = 'Activa'
                     FOR UPDATE;
                    IF NOT FOUND THEN
                        RAISE EXCEPTION 'Selected operator is unavailable.' USING ERRCODE = 'NXR01';
                    END IF;

                    PERFORM set_config('nexoruta.recipient_id', p_recipient_id::text, true);
                    PERFORM set_config('nexoruta.address_id', p_address_id::text, true);
                    PERFORM set_config('nexoruta.parcel_id', p_parcel_id::text, true);

                    INSERT INTO public."Destinatarios" ("Id", "OperadorId", "Nombre", "Email")
                    VALUES (p_recipient_id, v_operator_id, p_recipient_name, p_recipient_email);
                    INSERT INTO public."Direcciones" ("Id", "OperadorId", "Descripcion")
                    VALUES (p_address_id, v_operator_id, p_address);
                    INSERT INTO public."Envios" ("Id", "OperadorId", "ComercioId", "CreadoPorUsuarioId",
                        "DestinatarioId", "DireccionId", "Estado")
                    VALUES (p_shipment_id, v_operator_id, v_commerce_id, v_user_id,
                        p_recipient_id, p_address_id, 'Admitido');
                    INSERT INTO public."Bultos" ("Id", "OperadorId", "EnvioId", "Codigo", "PesoGramos",
                        "LargoCentimetros", "AnchoCentimetros", "AltoCentimetros")
                    VALUES (p_parcel_id, v_operator_id, p_shipment_id, p_parcel_code, p_weight,
                        p_length, p_width, p_height);
                    RETURN p_shipment_id;
                END
                $create_shipment$;
ALTER FUNCTION nexoruta_app.create_shipment(uuid, uuid, uuid, uuid, text, text, text, text, numeric, numeric, numeric, numeric) OWNER TO nexoruta_shipment_fn;
REVOKE ALL ON FUNCTION nexoruta_app.create_shipment(uuid, uuid, uuid, uuid, text, text, text, text, numeric, numeric, numeric, numeric) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nexoruta_app.create_shipment(uuid, uuid, uuid, uuid, text, text, text, text, numeric, numeric, numeric, numeric) TO nexoruta_api;
""");
}
