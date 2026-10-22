using Microsoft.EntityFrameworkCore.Migrations;

namespace NexoRuta.Infrastructure.Persistence.Migrations;

public partial class AuthorizeShipmentDestinationConfirmation
{
    private static void Configurar(MigrationBuilder migration) => migration.Sql("""
        DO $$ BEGIN IF NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='nexoruta_destino_fn') THEN
            CREATE ROLE nexoruta_destino_fn NOLOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
        END IF; END $$;
        DO $safe_roles$ BEGIN
            IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname IN ('nexoruta_destino_fn')
                AND (rolsuper OR rolbypassrls OR rolreplication)) THEN
                RAISE EXCEPTION 'Application roles must not have SUPERUSER, BYPASSRLS or REPLICATION; repair them with an authorized administrator.';
            END IF;
        END $safe_roles$;
        ALTER ROLE nexoruta_destino_fn NOLOGIN NOINHERIT NOCREATEDB NOCREATEROLE;
        GRANT USAGE ON SCHEMA public,nexoruta_app TO nexoruta_destino_fn;
        GRANT EXECUTE ON FUNCTION nexoruta_app.setting(text),nexoruta_app.ctx_uuid(text) TO nexoruta_destino_fn;
        GRANT SELECT ON public."Envios",public."Direcciones",public."Operadores",public."AccesosUsuario",public."Roles",public."RolesPermisos",public."Permisos" TO nexoruta_destino_fn;
        GRANT UPDATE ("Id") ON public."Operadores" TO nexoruta_destino_fn;
        GRANT UPDATE ("Latitud","Longitud","ConfirmadaPorUsuarioId","ConfirmadaEnUtc","Version") ON public."Direcciones" TO nexoruta_destino_fn;
        CREATE POLICY destination_shipment ON public."Envios" FOR SELECT TO nexoruta_destino_fn
            USING ("Id"=nexoruta_app.ctx_uuid('destination_shipment') AND nexoruta_app.setting('actor_kind')='comercio'
                AND "ComercioId"=nexoruta_app.ctx_uuid('org_id'));
        CREATE POLICY destination_address ON public."Direcciones" TO nexoruta_destino_fn
            USING ("Id"=nexoruta_app.ctx_uuid('destination_address') AND "OperadorId"=nexoruta_app.ctx_uuid('destination_operator'))
            WITH CHECK ("Id"=nexoruta_app.ctx_uuid('destination_address') AND "OperadorId"=nexoruta_app.ctx_uuid('destination_operator')
                AND "ConfirmadaPorUsuarioId"=nexoruta_app.ctx_uuid('user_id') AND "Latitud" BETWEEN -90 AND 90 AND "Longitud" BETWEEN -180 AND 180);
        CREATE POLICY destination_operator_lock ON public."Operadores" TO nexoruta_destino_fn
            USING ("Id"=nexoruta_app.ctx_uuid('destination_operator')) WITH CHECK(false);
        CREATE POLICY destination_access ON public."AccesosUsuario" FOR SELECT TO nexoruta_destino_fn
            USING ("Id"=nexoruta_app.ctx_uuid('access_id') AND "UsuarioId"=nexoruta_app.ctx_uuid('user_id')
                AND "ComercioId"=nexoruta_app.ctx_uuid('org_id') AND "Activo");
        CREATE POLICY destination_role ON public."Roles" FOR SELECT TO nexoruta_destino_fn
            USING (EXISTS(SELECT 1 FROM public."AccesosUsuario" a WHERE a."RolId"="Roles"."Id"));
        CREATE POLICY destination_grant ON public."RolesPermisos" FOR SELECT TO nexoruta_destino_fn
            USING (EXISTS(SELECT 1 FROM public."AccesosUsuario" a WHERE a."RolId"="RolesPermisos"."RolId"));
        CREATE POLICY destination_permission ON public."Permisos" FOR SELECT TO nexoruta_destino_fn USING ("Codigo"='envios.crear');

        CREATE FUNCTION nexoruta_app.confirm_shipment_destination(p_envio uuid,p_version bigint,p_lat double precision,p_lon double precision)
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
            -- La recepción y la planificación usan el mismo bloqueo; no confirmar un destino ya recibido.
            PERFORM 1 FROM public."Operadores" WHERE "Id"=e."OperadorId" FOR UPDATE;
            SELECT * INTO e FROM public."Envios" WHERE "Id"=p_envio;
            SELECT * INTO d FROM public."Direcciones" WHERE "Id"=e."DireccionId" AND "OperadorId"=e."OperadorId" FOR UPDATE;
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

    private static void Retirar(MigrationBuilder migration) => migration.Sql("""
        DROP FUNCTION nexoruta_app.confirm_shipment_destination(uuid,bigint,double precision,double precision);
        DROP POLICY destination_shipment ON public."Envios";
        DROP POLICY destination_address ON public."Direcciones";
        DROP POLICY destination_operator_lock ON public."Operadores";
        DROP POLICY destination_access ON public."AccesosUsuario";
        DROP POLICY destination_role ON public."Roles";
        DROP POLICY destination_grant ON public."RolesPermisos";
        DROP POLICY destination_permission ON public."Permisos";
        REVOKE SELECT ON public."Envios",public."Direcciones",public."Operadores",public."AccesosUsuario",public."Roles",public."RolesPermisos",public."Permisos" FROM nexoruta_destino_fn;
        REVOKE UPDATE ("Id") ON public."Operadores" FROM nexoruta_destino_fn;
        REVOKE UPDATE ("Latitud","Longitud","ConfirmadaPorUsuarioId","ConfirmadaEnUtc","Version") ON public."Direcciones" FROM nexoruta_destino_fn;
        """);
}
