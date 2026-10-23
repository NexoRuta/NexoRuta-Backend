using Microsoft.EntityFrameworkCore.Migrations;

namespace NexoRuta.Infrastructure.Persistence.Migrations;

public partial class AddOperatorVisualIdentity
{
    private static void ConfigurarIdentidadVisual(MigrationBuilder migration) => migration.Sql("""
        REVOKE ALL ON public."VersionesIdentidadVisualOperador" FROM PUBLIC;
        GRANT SELECT, INSERT ON public."VersionesIdentidadVisualOperador" TO nexoruta_api;
        GRANT SELECT ("OperadorId", "Version", "NombreComercial", "CorreoPublico", "TelefonoPublico",
            "ColorPrimario", "ColorSecundario", "LogoContenido", "LogoTipoContenido")
            ON public."VersionesIdentidadVisualOperador" TO nexoruta_public_fn;
        GRANT SELECT ("Id", "Nombre") ON public."Operadores" TO nexoruta_public_fn;

        ALTER TABLE public."VersionesIdentidadVisualOperador" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE public."VersionesIdentidadVisualOperador" FORCE ROW LEVEL SECURITY;
        CREATE POLICY identidad_visual_operator_read ON public."VersionesIdentidadVisualOperador"
            FOR SELECT TO nexoruta_api
            USING ((nexoruta_app.setting('actor_kind') = 'operador'
                    AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'))
                OR (nexoruta_app.setting('actor_kind') = 'comercio'
                    AND "OperadorId" = nexoruta_app.ctx_uuid('selected_operator_id')));
        CREATE POLICY identidad_visual_operator_insert ON public."VersionesIdentidadVisualOperador"
            FOR INSERT TO nexoruta_api
            WITH CHECK (nexoruta_app.setting('actor_kind') = 'operador'
                AND "OperadorId" = nexoruta_app.ctx_uuid('org_id')
                AND "AutorUsuarioId" = nexoruta_app.ctx_uuid('user_id'));
        CREATE POLICY identidad_visual_public_read ON public."VersionesIdentidadVisualOperador"
            FOR SELECT TO nexoruta_public_fn
            USING ("OperadorId" = nexoruta_app.ctx_uuid('public_operator_id'));
        CREATE POLICY identidad_visual_public_operator_read ON public."Operadores"
            FOR SELECT TO nexoruta_public_fn
            USING ("Id" = nexoruta_app.ctx_uuid('public_operator_id'));

        DROP FUNCTION nexoruta_app.public_tracking(text);
        CREATE FUNCTION nexoruta_app.public_tracking(p_token text)
        RETURNS TABLE("Estado" text, "NombreOperador" text, "CorreoPublico" text,
            "TelefonoPublico" text, "ColorPrimario" text, "ColorSecundario" text,
            "LogoContenido" bytea, "LogoTipoContenido" text)
        LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
        AS $tracking$
        DECLARE
            v_hash text;
            v_operator_id uuid;
            v_operator_name text;
            v_state text;
        BEGIN
            IF p_token IS NULL OR length(p_token) <> 64 OR p_token !~ '^[0-9a-f]{64}$' THEN RETURN; END IF;
            v_hash := encode(public.digest(convert_to(p_token, 'SQL_ASCII'), 'sha256'), 'hex');
            PERFORM set_config('nexoruta.public_token_hash', v_hash, true);
            SELECT e."Estado"::text, e."OperadorId" INTO v_state, v_operator_id
              FROM public."Envios" e
             WHERE EXISTS (SELECT 1 FROM public."EnlacesSeguimiento" l
                WHERE l."EnvioId" = e."Id" AND l."OperadorId" = e."OperadorId"
                  AND l."TokenHash" = v_hash AND l."RevocadoEnUtc" IS NULL AND l."ExpiraEnUtc" > now());
            IF NOT FOUND THEN RETURN; END IF;
            PERFORM set_config('nexoruta.public_operator_id', v_operator_id::text, true);
            SELECT o."Nombre" INTO v_operator_name FROM public."Operadores" o WHERE o."Id" = v_operator_id;
            RETURN QUERY
            SELECT v_state::text, COALESCE(identity."NombreComercial", v_operator_name)::text,
                identity."CorreoPublico"::text, identity."TelefonoPublico"::text,
                identity."ColorPrimario"::text, identity."ColorSecundario"::text,
                identity."LogoContenido"::bytea, identity."LogoTipoContenido"::text
              FROM (SELECT 1) AS one_row
              LEFT JOIN LATERAL (
                SELECT i."NombreComercial", i."CorreoPublico", i."TelefonoPublico", i."ColorPrimario",
                    i."ColorSecundario", i."LogoContenido", i."LogoTipoContenido"
                  FROM public."VersionesIdentidadVisualOperador" i
                 WHERE i."OperadorId" = v_operator_id
                 ORDER BY i."Version" DESC LIMIT 1
              ) identity ON true;
        END
        $tracking$;
        ALTER FUNCTION nexoruta_app.public_tracking(text) OWNER TO nexoruta_public_fn;
        REVOKE ALL ON FUNCTION nexoruta_app.public_tracking(text) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION nexoruta_app.public_tracking(text) TO nexoruta_api;
        """);

    private static void RetirarIdentidadVisual(MigrationBuilder migration) => migration.Sql("""
        DROP FUNCTION nexoruta_app.public_tracking(text);
        CREATE FUNCTION nexoruta_app.public_tracking(p_token text)
        RETURNS TABLE("Estado" text)
        LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
        AS $tracking$
        DECLARE v_hash text;
        BEGIN
            IF p_token IS NULL OR length(p_token) <> 64 OR p_token !~ '^[0-9a-f]{64}$' THEN RETURN; END IF;
            v_hash := encode(public.digest(convert_to(p_token, 'SQL_ASCII'), 'sha256'), 'hex');
            PERFORM set_config('nexoruta.public_token_hash', v_hash, true);
            RETURN QUERY SELECT e."Estado"::text FROM public."Envios" e
             WHERE EXISTS (SELECT 1 FROM public."EnlacesSeguimiento" l
                WHERE l."EnvioId" = e."Id" AND l."OperadorId" = e."OperadorId"
                  AND l."TokenHash" = v_hash AND l."RevocadoEnUtc" IS NULL AND l."ExpiraEnUtc" > now());
        END
        $tracking$;
        ALTER FUNCTION nexoruta_app.public_tracking(text) OWNER TO nexoruta_public_fn;
        REVOKE ALL ON FUNCTION nexoruta_app.public_tracking(text) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION nexoruta_app.public_tracking(text) TO nexoruta_api;
        DROP POLICY identidad_visual_operator_read ON public."VersionesIdentidadVisualOperador";
        DROP POLICY identidad_visual_operator_insert ON public."VersionesIdentidadVisualOperador";
        DROP POLICY identidad_visual_public_read ON public."VersionesIdentidadVisualOperador";
        DROP POLICY identidad_visual_public_operator_read ON public."Operadores";
        ALTER TABLE public."VersionesIdentidadVisualOperador" NO FORCE ROW LEVEL SECURITY;
        ALTER TABLE public."VersionesIdentidadVisualOperador" DISABLE ROW LEVEL SECURITY;
        REVOKE ALL ON public."VersionesIdentidadVisualOperador" FROM nexoruta_api, nexoruta_public_fn;
        """);
}
