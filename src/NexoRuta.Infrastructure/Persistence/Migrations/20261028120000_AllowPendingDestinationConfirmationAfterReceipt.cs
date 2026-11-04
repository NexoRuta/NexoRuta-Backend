using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Persistence.Migrations;

[DbContext(typeof(NexoRutaDbContext))]
[Migration("20261028120000_AllowPendingDestinationConfirmationAfterReceipt")]
public partial class AllowPendingDestinationConfirmationAfterReceipt : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE OR REPLACE FUNCTION nexoruta_app.confirm_shipment_destination(p_envio uuid,p_version bigint,p_lat double precision,p_lon double precision)
        RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,nexoruta_app AS $$
        DECLARE e public."Envios"%ROWTYPE; d public."Direcciones"%ROWTYPE;
        BEGIN
            IF nexoruta_app.setting('actor_kind') IS DISTINCT FROM 'comercio' OR NOT EXISTS (
                SELECT 1 FROM public."AccesosUsuario" a JOIN public."Roles" r ON r."Id"=a."RolId"
                JOIN public."RolesPermisos" rp ON rp."RolId"=r."Id" JOIN public."Permisos" p ON p."Id"=rp."PermisoId"
                WHERE p."Codigo"='envios.crear') THEN RAISE EXCEPTION 'Destination confirmation is not authorized.' USING ERRCODE='42501'; END IF;
            IF p_version IS NULL OR p_version<0 OR p_lat IS NULL OR p_lon IS NULL OR NOT p_lat BETWEEN -90 AND 90 OR NOT p_lon BETWEEN -180 AND 180 THEN
                RAISE EXCEPTION 'Invalid destination coordinates or version.' USING ERRCODE='22023'; END IF;
            PERFORM set_config('nexoruta.destination_shipment',p_envio::text,true);
            SELECT * INTO e FROM public."Envios" WHERE "Id"=p_envio;
            IF NOT FOUND THEN RETURN false; END IF;
            PERFORM set_config('nexoruta.destination_operator',e."OperadorId"::text,true);
            PERFORM set_config('nexoruta.destination_address',e."DireccionId"::text,true);
            -- Reception and planning share this operator lock; only a pending destination can be confirmed after reception.
            PERFORM 1 FROM public."Operadores" WHERE "Id"=e."OperadorId" FOR UPDATE;
            SELECT * INTO e FROM public."Envios" WHERE "Id"=p_envio;
            IF NOT FOUND THEN RAISE EXCEPTION 'Destination or shipment changed.' USING ERRCODE='NXR02'; END IF;
            SELECT * INTO d FROM public."Direcciones" WHERE "Id"=e."DireccionId" AND "OperadorId"=e."OperadorId" FOR UPDATE;
            IF NOT FOUND THEN RAISE EXCEPTION 'Destination or shipment changed.' USING ERRCODE='NXR02'; END IF;
            IF NOT (e."Estado"='Admitido' OR (e."Estado"='EnDeposito' AND d."ConfirmadaEnUtc" IS NULL)) OR d."Version"<>p_version THEN
                RAISE EXCEPTION 'Destination or shipment changed.' USING ERRCODE='NXR02'; END IF;
            UPDATE public."Direcciones" SET "Latitud"=p_lat,"Longitud"=p_lon,"ConfirmadaPorUsuarioId"=nexoruta_app.ctx_uuid('user_id'),
                "ConfirmadaEnUtc"=clock_timestamp(),"Version"=p_version+1 WHERE "Id"=d."Id";
            RETURN FOUND;
        END $$;
        ALTER FUNCTION nexoruta_app.confirm_shipment_destination(uuid,bigint,double precision,double precision) OWNER TO nexoruta_destino_fn;
        REVOKE ALL ON FUNCTION nexoruta_app.confirm_shipment_destination(uuid,bigint,double precision,double precision) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION nexoruta_app.confirm_shipment_destination(uuid,bigint,double precision,double precision) TO nexoruta_api;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE OR REPLACE FUNCTION nexoruta_app.confirm_shipment_destination(p_envio uuid,p_version bigint,p_lat double precision,p_lon double precision)
        RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,nexoruta_app AS $$
        DECLARE e public."Envios"%ROWTYPE; d public."Direcciones"%ROWTYPE;
        BEGIN
            IF nexoruta_app.setting('actor_kind') IS DISTINCT FROM 'comercio' OR NOT EXISTS (
                SELECT 1 FROM public."AccesosUsuario" a JOIN public."Roles" r ON r."Id"=a."RolId"
                JOIN public."RolesPermisos" rp ON rp."RolId"=r."Id" JOIN public."Permisos" p ON p."Id"=rp."PermisoId"
                WHERE p."Codigo"='envios.crear') THEN RAISE EXCEPTION 'Destination confirmation is not authorized.' USING ERRCODE='42501'; END IF;
            IF p_version IS NULL OR p_version<0 OR p_lat IS NULL OR p_lon IS NULL OR NOT p_lat BETWEEN -90 AND 90 OR NOT p_lon BETWEEN -180 AND 180 THEN
                RAISE EXCEPTION 'Invalid destination coordinates or version.' USING ERRCODE='22023'; END IF;
            PERFORM set_config('nexoruta.destination_shipment',p_envio::text,true);
            SELECT * INTO e FROM public."Envios" WHERE "Id"=p_envio;
            IF NOT FOUND THEN RETURN false; END IF;
            PERFORM set_config('nexoruta.destination_operator',e."OperadorId"::text,true);
            PERFORM set_config('nexoruta.destination_address',e."DireccionId"::text,true);
            PERFORM 1 FROM public."Operadores" WHERE "Id"=e."OperadorId" FOR UPDATE;
            SELECT * INTO e FROM public."Envios" WHERE "Id"=p_envio;
            IF NOT FOUND THEN RAISE EXCEPTION 'Destination or shipment changed.' USING ERRCODE='NXR02'; END IF;
            SELECT * INTO d FROM public."Direcciones" WHERE "Id"=e."DireccionId" AND "OperadorId"=e."OperadorId" FOR UPDATE;
            IF NOT FOUND THEN RAISE EXCEPTION 'Destination or shipment changed.' USING ERRCODE='NXR02'; END IF;
            IF e."Estado"<>'Admitido' OR d."Version"<>p_version THEN
                RAISE EXCEPTION 'Destination or shipment changed.' USING ERRCODE='NXR02'; END IF;
            UPDATE public."Direcciones" SET "Latitud"=p_lat,"Longitud"=p_lon,"ConfirmadaPorUsuarioId"=nexoruta_app.ctx_uuid('user_id'),
                "ConfirmadaEnUtc"=clock_timestamp(),"Version"=p_version+1 WHERE "Id"=d."Id";
            RETURN FOUND;
        END $$;
        ALTER FUNCTION nexoruta_app.confirm_shipment_destination(uuid,bigint,double precision,double precision) OWNER TO nexoruta_destino_fn;
        REVOKE ALL ON FUNCTION nexoruta_app.confirm_shipment_destination(uuid,bigint,double precision,double precision) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION nexoruta_app.confirm_shipment_destination(uuid,bigint,double precision,double precision) TO nexoruta_api;
        """);
}
