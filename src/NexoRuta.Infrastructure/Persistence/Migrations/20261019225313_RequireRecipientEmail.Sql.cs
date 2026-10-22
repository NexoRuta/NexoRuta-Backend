using Microsoft.EntityFrameworkCore.Migrations;
namespace NexoRuta.Infrastructure.Persistence.Migrations;

public partial class RequireRecipientEmail
{
    private static void ConfigureEmailInput(MigrationBuilder migration) => migration.Sql("""
DROP FUNCTION nexoruta_app.create_shipment(uuid, uuid, uuid, uuid, text, text, text, numeric, numeric, numeric, numeric);
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
    private static void RestoreEmailInput(MigrationBuilder migration) => migration.Sql("""
DROP FUNCTION nexoruta_app.create_shipment(uuid, uuid, uuid, uuid, text, text, text, text, numeric, numeric, numeric, numeric);
CREATE OR REPLACE FUNCTION nexoruta_app.create_shipment(
                    p_shipment_id uuid, p_recipient_id uuid, p_address_id uuid, p_parcel_id uuid,
                    p_recipient_name text, p_address text, p_parcel_code text,
                    p_weight numeric, p_length numeric, p_width numeric, p_height numeric)
                RETURNS uuid
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
                AS $create_shipment$
                DECLARE v_commerce_id uuid := nexoruta_app.ctx_uuid('org_id');
                        v_operator_id uuid := nexoruta_app.ctx_uuid('selected_operator_id');
                        v_user_id uuid := nexoruta_app.ctx_uuid('user_id');
                BEGIN
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

                    INSERT INTO public."Destinatarios" ("Id", "OperadorId", "Nombre")
                    VALUES (p_recipient_id, v_operator_id, p_recipient_name);
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
ALTER FUNCTION nexoruta_app.create_shipment(uuid, uuid, uuid, uuid, text, text, text, numeric, numeric, numeric, numeric) OWNER TO nexoruta_shipment_fn;
REVOKE ALL ON FUNCTION nexoruta_app.create_shipment(uuid, uuid, uuid, uuid, text, text, text, numeric, numeric, numeric, numeric) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nexoruta_app.create_shipment(uuid, uuid, uuid, uuid, text, text, text, numeric, numeric, numeric, numeric) TO nexoruta_api;
""");
}
