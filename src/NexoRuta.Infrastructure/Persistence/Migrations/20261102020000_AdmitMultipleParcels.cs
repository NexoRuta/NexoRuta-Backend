using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace NexoRuta.Infrastructure.Persistence.Migrations;
[DbContext(typeof(NexoRutaDbContext))]
[Migration("20261102020000_AdmitMultipleParcels")]
public sealed class AdmitMultipleParcels : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
DROP FUNCTION nexoruta_app.create_shipment(uuid, uuid, uuid, uuid, text, text, text, text, numeric, numeric, numeric, numeric, uuid);
CREATE OR REPLACE FUNCTION nexoruta_app.create_shipment(
                    p_shipment_id uuid, p_recipient_id uuid, p_address_id uuid,
                    p_recipient_name text, p_recipient_email text, p_address text, p_parcels jsonb, p_rules_version uuid)
                RETURNS uuid
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
                AS $create_shipment$
                DECLARE v_commerce_id uuid := nexoruta_app.ctx_uuid('org_id');
                        v_operator_id uuid := nexoruta_app.ctx_uuid('selected_operator_id');
                        v_user_id uuid := nexoruta_app.ctx_uuid('user_id');
                        parcel jsonb;
                BEGIN
                    IF p_recipient_email IS NULL OR length(p_recipient_email) > 254
                       OR p_recipient_email <> btrim(p_recipient_email)
                       OR p_recipient_email !~ '^[^[:space:]@]+@[^[:space:]@]+$' THEN
                        RAISE EXCEPTION 'A valid recipient email is required.' USING ERRCODE = '22023';
                    END IF;
                    IF nexoruta_app.setting('actor_kind') <> 'comercio' OR v_commerce_id IS NULL
                       OR v_operator_id IS NULL OR v_user_id IS NULL OR p_shipment_id IS NULL
                       OR p_recipient_id IS NULL OR p_address_id IS NULL
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


                    INSERT INTO public."Destinatarios" ("Id", "OperadorId", "Nombre", "Email")
                    VALUES (p_recipient_id, v_operator_id, p_recipient_name, p_recipient_email);
                    INSERT INTO public."Direcciones" ("Id", "OperadorId", "Descripcion")
                    VALUES (p_address_id, v_operator_id, p_address);
                    INSERT INTO public."Envios" ("Id", "OperadorId", "ComercioId", "CreadoPorUsuarioId",
                        "DestinatarioId", "DireccionId", "VersionReglasId", "Estado")
                    VALUES (p_shipment_id, v_operator_id, v_commerce_id, v_user_id,
                        p_recipient_id, p_address_id, p_rules_version, 'Admitido');
                    IF p_parcels IS NULL OR jsonb_typeof(p_parcels) <> 'array' OR jsonb_array_length(p_parcels)=0 THEN
                        RAISE EXCEPTION 'At least one parcel is required.' USING ERRCODE='22023';
                    END IF;
                    IF EXISTS (SELECT 1 FROM jsonb_array_elements(p_parcels) p
                        GROUP BY btrim(p->>'Codigo') HAVING count(*)>1) THEN
                        RAISE EXCEPTION 'Duplicate parcel code.' USING ERRCODE='22023';
                    END IF;
                    FOR parcel IN SELECT value FROM jsonb_array_elements(p_parcels) LOOP
                        IF parcel->>'Id' IS NULL OR nullif(btrim(parcel->>'Codigo'),'') IS NULL
                            OR length(btrim(parcel->>'Codigo'))>80 OR EXISTS (
                                SELECT 1 FROM (VALUES ('PesoGramos'),('LargoCentimetros'),('AnchoCentimetros'),('AltoCentimetros')) measures(name)
                                WHERE parcel->>name IS NULL OR (parcel->>name)::numeric NOT BETWEEN 0.01 AND 999999999
                                    OR round((parcel->>name)::numeric,2)<>(parcel->>name)::numeric) THEN
                            RAISE EXCEPTION 'Invalid parcel declaration.' USING ERRCODE='22023';
                        END IF;
                        PERFORM set_config('nexoruta.parcel_id',parcel->>'Id',true);
                        INSERT INTO public."Bultos" ("Id", "OperadorId", "EnvioId", "Codigo", "PesoGramos",
                            "LargoCentimetros", "AnchoCentimetros", "AltoCentimetros")
                        VALUES ((parcel->>'Id')::uuid, v_operator_id, p_shipment_id, btrim(parcel->>'Codigo'),
                            (parcel->>'PesoGramos')::numeric, (parcel->>'LargoCentimetros')::numeric,
                            (parcel->>'AnchoCentimetros')::numeric, (parcel->>'AltoCentimetros')::numeric);
                    END LOOP;
                    RETURN p_shipment_id;
                END
                $create_shipment$;
ALTER FUNCTION nexoruta_app.create_shipment(uuid, uuid, uuid, text, text, text, jsonb, uuid) OWNER TO nexoruta_shipment_fn;
REVOKE ALL ON FUNCTION nexoruta_app.create_shipment(uuid, uuid, uuid, text, text, text, jsonb, uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nexoruta_app.create_shipment(uuid, uuid, uuid, text, text, text, jsonb, uuid) TO nexoruta_api;
""");
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
DROP FUNCTION nexoruta_app.create_shipment(uuid, uuid, uuid, text, text, text, jsonb, uuid);
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
}
