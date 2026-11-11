using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NexoRuta.Infrastructure.Persistence.Migrations;

[DbContext(typeof(NexoRutaDbContext))]
[Migration("20261109010000_AP04CommerceReturns")]
public sealed class AP04CommerceReturns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE public."Envios"
            ADD CONSTRAINT "AK_Envios_Operador_Comercio_Id" UNIQUE ("OperadorId", "ComercioId", "Id");
        ALTER TABLE public."Bultos"
            ADD CONSTRAINT "AK_Bultos_Operador_Envio_Id" UNIQUE ("OperadorId", "EnvioId", "Id");

        ALTER POLICY permissions_catalog_insert ON public."Permisos"
            WITH CHECK (nexoruta_app.setting('actor_kind') IN ('plataforma','identidad','bootstrap')
                AND "Codigo" IN ('usuarios.gestionar','configuracion.gestionar','envios.crear',
                    'envios.consultar','envios.gestionar','plataforma.organizaciones.gestionar',
                    'reparto.ejecutar','devoluciones.gestionar'));

        INSERT INTO public."Permisos" ("Id", "Codigo", "Nombre")
        VALUES (gen_random_uuid(), 'devoluciones.gestionar', 'devoluciones.gestionar')
        ON CONFLICT ("Codigo") DO NOTHING;

        INSERT INTO public."RolesPermisos" ("RolId", "PermisoId")
        SELECT a."RolId", p."Id"
        FROM public."AccesosUsuario" a
        JOIN public."Roles" r ON r."Id" = a."RolId"
        CROSS JOIN public."Permisos" p
        WHERE a."EsPropietario" AND a."Activo" AND a."ComercioId" IS NOT NULL
          AND r."ComercioId" = a."ComercioId" AND r."EsProtegidoSistema"
          AND p."Codigo" = 'devoluciones.gestionar'
        ON CONFLICT DO NOTHING;

        CREATE TABLE public."TransferenciasDevolucion" (
            "Id" uuid PRIMARY KEY,
            "ComandoId" uuid NOT NULL,
            "OperadorId" uuid NOT NULL,
            "ComercioId" uuid NOT NULL,
            "EnvioId" uuid NOT NULL,
            "VersionEnvioAnterior" bigint NOT NULL CHECK ("VersionEnvioAnterior" >= 0),
            "VersionEnvioNueva" bigint NOT NULL CHECK ("VersionEnvioNueva" > "VersionEnvioAnterior"),
            "AccesoId" uuid NOT NULL,
            "UsuarioId" uuid NOT NULL,
            "HuellaSolicitud" bytea NOT NULL CHECK (octet_length("HuellaSolicitud") = 32),
            "RegistradaEnUtc" timestamptz NOT NULL DEFAULT clock_timestamp(),
            CONSTRAINT "AK_TransferenciasDevolucion_Operador_Id" UNIQUE ("OperadorId", "Id"),
            CONSTRAINT "AK_TransferenciasDevolucion_Tenant" UNIQUE ("OperadorId", "ComercioId", "EnvioId", "Id"),
            CONSTRAINT "AK_TransferenciasDevolucion_Comando" UNIQUE ("OperadorId", "ComandoId"),
            CONSTRAINT "FK_TransferenciasDevolucion_Envio" FOREIGN KEY ("OperadorId", "ComercioId", "EnvioId")
                REFERENCES public."Envios" ("OperadorId", "ComercioId", "Id") ON DELETE RESTRICT,
            CHECK ("Id" = "ComandoId")
        );

        CREATE TABLE public."TransferenciasDevolucionBultos" (
            "OperadorId" uuid NOT NULL,
            "ComercioId" uuid NOT NULL,
            "EnvioId" uuid NOT NULL,
            "TransferenciaId" uuid NOT NULL,
            "BultoId" uuid NOT NULL,
            "VersionAnterior" bigint NOT NULL CHECK ("VersionAnterior" >= 0),
            "VersionNueva" bigint NOT NULL CHECK ("VersionNueva" > "VersionAnterior"),
            "ResultadoAnterior" text NOT NULL CHECK ("ResultadoAnterior" IN ('EnDevolucion','Devuelto')),
            "ResultadoNuevo" text NOT NULL CHECK ("ResultadoNuevo" = 'Devuelto'),
            "CustodiaAnterior" text NOT NULL CHECK ("CustodiaAnterior" = 'Deposito'),
            "CustodiaNueva" text NOT NULL CHECK ("CustodiaNueva" = 'Comercio'),
            PRIMARY KEY ("TransferenciaId", "BultoId"),
            CONSTRAINT "FK_TransferenciasDevolucionBultos_Transferencia" FOREIGN KEY ("OperadorId", "ComercioId", "EnvioId", "TransferenciaId")
                REFERENCES public."TransferenciasDevolucion" ("OperadorId", "ComercioId", "EnvioId", "Id") ON DELETE RESTRICT,
            CONSTRAINT "FK_TransferenciasDevolucionBultos_Bulto" FOREIGN KEY ("OperadorId", "EnvioId", "BultoId")
                REFERENCES public."Bultos" ("OperadorId", "EnvioId", "Id") ON DELETE RESTRICT,
            CONSTRAINT "AK_TransferenciasDevolucionBultos_Bulto" UNIQUE ("OperadorId", "BultoId"),
            CHECK ("VersionNueva" = "VersionAnterior" + CASE WHEN "ResultadoAnterior" = 'EnDevolucion' THEN 2 ELSE 1 END)
        );

        CREATE TABLE public."AcusesDevolucionComercio" (
            "Id" uuid PRIMARY KEY,
            "ComandoId" uuid NOT NULL,
            "OperadorId" uuid NOT NULL,
            "ComercioId" uuid NOT NULL,
            "EnvioId" uuid NOT NULL,
            "TransferenciaId" uuid NOT NULL,
            "VersionTransferencia" bigint NOT NULL CHECK ("VersionTransferencia" = 1),
            "Resultado" text NOT NULL CHECK ("Resultado" IN ('Conforme','ConObservaciones')),
            "AccesoId" uuid NOT NULL,
            "UsuarioId" uuid NOT NULL,
            "HuellaSolicitud" bytea NOT NULL CHECK (octet_length("HuellaSolicitud") = 32),
            "RegistradoEnUtc" timestamptz NOT NULL DEFAULT clock_timestamp(),
            CONSTRAINT "AK_AcusesDevolucionComercio_Tenant" UNIQUE ("OperadorId", "ComercioId", "EnvioId", "Id"),
            CONSTRAINT "AK_AcusesDevolucionComercio_Comando" UNIQUE ("ComercioId", "ComandoId"),
            CONSTRAINT "FK_AcusesDevolucionComercio_Transferencia" FOREIGN KEY ("OperadorId", "ComercioId", "EnvioId", "TransferenciaId")
                REFERENCES public."TransferenciasDevolucion" ("OperadorId", "ComercioId", "EnvioId", "Id") ON DELETE RESTRICT,
            CHECK ("Id" = "ComandoId")
        );

        CREATE TABLE public."AcusesDevolucionBultos" (
            "OperadorId" uuid NOT NULL,
            "ComercioId" uuid NOT NULL,
            "EnvioId" uuid NOT NULL,
            "AcuseId" uuid NOT NULL,
            "TransferenciaId" uuid NOT NULL,
            "BultoId" uuid NOT NULL,
            "VersionBulto" bigint NOT NULL CHECK ("VersionBulto" >= 0),
            "Observacion" varchar(500),
            PRIMARY KEY ("AcuseId", "BultoId"),
            CONSTRAINT "FK_AcusesDevolucionBultos_Acuse" FOREIGN KEY ("OperadorId", "ComercioId", "EnvioId", "AcuseId")
                REFERENCES public."AcusesDevolucionComercio" ("OperadorId", "ComercioId", "EnvioId", "Id") ON DELETE RESTRICT,
            CONSTRAINT "FK_AcusesDevolucionBultos_TransferenciaBulto" FOREIGN KEY ("TransferenciaId", "BultoId")
                REFERENCES public."TransferenciasDevolucionBultos" ("TransferenciaId", "BultoId") ON DELETE RESTRICT,
            CONSTRAINT "AK_AcusesDevolucionBultos_Transferencia_Bulto" UNIQUE ("TransferenciaId", "BultoId"),
            CHECK ("Observacion" IS NULL OR length(btrim("Observacion")) BETWEEN 10 AND 500)
        );

        CREATE INDEX "IX_TransferenciasDevolucion_Comercio_Actividad" ON public."TransferenciasDevolucion" ("ComercioId", "RegistradaEnUtc" DESC, "Id" DESC);
        CREATE INDEX "IX_TransferenciasDevolucion_Operador_Actividad" ON public."TransferenciasDevolucion" ("OperadorId", "RegistradaEnUtc" DESC, "Id" DESC);
        CREATE INDEX "IX_TransferenciasDevolucion_Envio" ON public."TransferenciasDevolucion" ("OperadorId", "EnvioId", "RegistradaEnUtc", "Id");
        CREATE INDEX "IX_AcusesDevolucionComercio_Envio" ON public."AcusesDevolucionComercio" ("OperadorId", "EnvioId", "RegistradoEnUtc", "Id");

        ALTER TABLE public."TransferenciasDevolucion" OWNER TO nexoruta_schema_owner;
        ALTER TABLE public."TransferenciasDevolucionBultos" OWNER TO nexoruta_schema_owner;
        ALTER TABLE public."AcusesDevolucionComercio" OWNER TO nexoruta_schema_owner;
        ALTER TABLE public."AcusesDevolucionBultos" OWNER TO nexoruta_schema_owner;

        DO $m$
        DECLARE t text;
        BEGIN
            FOREACH t IN ARRAY ARRAY['TransferenciasDevolucion','TransferenciasDevolucionBultos','AcusesDevolucionComercio','AcusesDevolucionBultos'] LOOP
                EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', t);
                EXECUTE format('ALTER TABLE public.%I FORCE ROW LEVEL SECURITY', t);
                EXECUTE format('GRANT SELECT, INSERT ON public.%I TO nexoruta_api', t);
            END LOOP;
        END
        $m$;

        CREATE POLICY return_transfer_read ON public."TransferenciasDevolucion" FOR SELECT TO nexoruta_api
            USING ((nexoruta_app.setting('actor_kind')='comercio' AND "ComercioId"=nexoruta_app.ctx_uuid('org_id'))
                OR (nexoruta_app.setting('actor_kind')='operador' AND "OperadorId"=nexoruta_app.ctx_uuid('org_id')));
        CREATE POLICY return_transfer_insert ON public."TransferenciasDevolucion" FOR INSERT TO nexoruta_api
            WITH CHECK (nexoruta_app.setting('actor_kind')='operador' AND "OperadorId"=nexoruta_app.ctx_uuid('org_id')
                AND "AccesoId"=nexoruta_app.ctx_uuid('access_id') AND "UsuarioId"=nexoruta_app.ctx_uuid('user_id')
                AND EXISTS (SELECT 1 FROM public."AccesosUsuario" a JOIN public."RolesPermisos" rp ON rp."RolId"=a."RolId"
                    JOIN public."Permisos" p ON p."Id"=rp."PermisoId" WHERE a."Id"="AccesoId" AND a."UsuarioId"="UsuarioId"
                    AND a."OperadorId"="OperadorId" AND a."Activo" AND p."Codigo"='envios.gestionar'));
        CREATE POLICY return_transfer_line_read ON public."TransferenciasDevolucionBultos" FOR SELECT TO nexoruta_api
            USING (EXISTS (SELECT 1 FROM public."TransferenciasDevolucion" t WHERE t."Id"="TransferenciaId"
                AND t."OperadorId"="OperadorId" AND t."ComercioId"="ComercioId" AND t."EnvioId"="EnvioId"));
        CREATE POLICY return_transfer_line_insert ON public."TransferenciasDevolucionBultos" FOR INSERT TO nexoruta_api
            WITH CHECK (EXISTS (SELECT 1 FROM public."TransferenciasDevolucion" t WHERE t."Id"="TransferenciaId"
                AND t."OperadorId"="OperadorId" AND t."ComercioId"="ComercioId" AND t."EnvioId"="EnvioId"));

        CREATE POLICY return_ack_read ON public."AcusesDevolucionComercio" FOR SELECT TO nexoruta_api
            USING ((nexoruta_app.setting('actor_kind')='comercio' AND "ComercioId"=nexoruta_app.ctx_uuid('org_id'))
                OR (nexoruta_app.setting('actor_kind')='operador' AND "OperadorId"=nexoruta_app.ctx_uuid('org_id')));
        CREATE POLICY return_ack_insert ON public."AcusesDevolucionComercio" FOR INSERT TO nexoruta_api
            WITH CHECK (nexoruta_app.setting('actor_kind')='comercio' AND "ComercioId"=nexoruta_app.ctx_uuid('org_id')
                AND "AccesoId"=nexoruta_app.ctx_uuid('access_id') AND "UsuarioId"=nexoruta_app.ctx_uuid('user_id')
                AND EXISTS (SELECT 1 FROM public."AccesosUsuario" a JOIN public."RolesPermisos" rp ON rp."RolId"=a."RolId"
                    JOIN public."Permisos" p ON p."Id"=rp."PermisoId" WHERE a."Id"="AccesoId" AND a."UsuarioId"="UsuarioId"
                    AND a."ComercioId"="ComercioId" AND a."Activo" AND p."Codigo"='devoluciones.gestionar'));
        CREATE POLICY return_ack_line_read ON public."AcusesDevolucionBultos" FOR SELECT TO nexoruta_api
            USING (EXISTS (SELECT 1 FROM public."AcusesDevolucionComercio" a WHERE a."Id"="AcuseId"
                AND a."OperadorId"="OperadorId" AND a."ComercioId"="ComercioId" AND a."EnvioId"="EnvioId"));
        CREATE POLICY return_ack_line_insert ON public."AcusesDevolucionBultos" FOR INSERT TO nexoruta_api
            WITH CHECK (EXISTS (SELECT 1 FROM public."AcusesDevolucionComercio" a WHERE a."Id"="AcuseId"
                AND a."OperadorId"="OperadorId" AND a."ComercioId"="ComercioId" AND a."EnvioId"="EnvioId"));

        CREATE FUNCTION public.validate_return_transfer() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
        DECLARE v_access public."AccesosUsuario"; v_permission text; v_shipment public."Envios"; v_parcel public."Bultos";
        BEGIN
            IF TG_TABLE_NAME='TransferenciasDevolucion' THEN
                v_permission := 'envios.gestionar';
                IF nexoruta_app.setting('actor_kind') IS DISTINCT FROM 'operador'
                    OR NEW."OperadorId" IS DISTINCT FROM nexoruta_app.ctx_uuid('org_id')
                    OR NEW."AccesoId" IS DISTINCT FROM nexoruta_app.ctx_uuid('access_id')
                    OR NEW."UsuarioId" IS DISTINCT FROM nexoruta_app.ctx_uuid('user_id') THEN
                    RAISE EXCEPTION 'Return transfer actor mismatch' USING ERRCODE='42501';
                END IF;
                SELECT * INTO v_shipment FROM public."Envios" e WHERE e."OperadorId"=NEW."OperadorId"
                    AND e."ComercioId"=NEW."ComercioId" AND e."Id"=NEW."EnvioId" AND NOT e."EsRetiro";
                IF NOT FOUND OR NOT EXISTS (SELECT 1 FROM public."EnvioEventos" h WHERE h."OperadorId"=NEW."OperadorId"
                    AND h."ComercioId"=NEW."ComercioId" AND h."EnvioId"=NEW."EnvioId"
                    AND h."OrigenTipo"='DevolucionBultosComercio' AND h."OrigenId"=NEW."ComandoId"
                    AND h."VersionAnterior"=NEW."VersionEnvioAnterior" AND h."VersionNueva"=NEW."VersionEnvioNueva"
                    AND h."ActorAccesoId"=NEW."AccesoId" AND h."ActorUsuarioId"=NEW."UsuarioId") THEN
                    RAISE EXCEPTION 'Return transfer has no matching canonical physical transition' USING ERRCODE='23514';
                END IF;
            ELSE
                v_permission := 'devoluciones.gestionar';
                IF nexoruta_app.setting('actor_kind') IS DISTINCT FROM 'comercio'
                    OR NEW."ComercioId" IS DISTINCT FROM nexoruta_app.ctx_uuid('org_id')
                    OR NEW."AccesoId" IS DISTINCT FROM nexoruta_app.ctx_uuid('access_id')
                    OR NEW."UsuarioId" IS DISTINCT FROM nexoruta_app.ctx_uuid('user_id') THEN
                    RAISE EXCEPTION 'Return acknowledgement actor mismatch' USING ERRCODE='42501';
                END IF;
                IF NOT EXISTS (SELECT 1 FROM public."TransferenciasDevolucion" t WHERE t."Id"=NEW."TransferenciaId"
                    AND t."OperadorId"=NEW."OperadorId" AND t."ComercioId"=NEW."ComercioId" AND t."EnvioId"=NEW."EnvioId") THEN
                    RAISE EXCEPTION 'Return acknowledgement has no matching transfer' USING ERRCODE='23514';
                END IF;
            END IF;
            SELECT a.* INTO v_access FROM public."AccesosUsuario" a WHERE a."Id"=NEW."AccesoId"
                AND a."UsuarioId"=NEW."UsuarioId" AND a."Activo";
            IF NOT FOUND OR NOT EXISTS (SELECT 1 FROM public."RolesPermisos" rp JOIN public."Permisos" p ON p."Id"=rp."PermisoId"
                WHERE rp."RolId"=v_access."RolId" AND p."Codigo"=v_permission) THEN
                RAISE EXCEPTION 'Return operation permission is no longer active' USING ERRCODE='42501';
            END IF;
            RETURN NEW;
        END $$;
        ALTER FUNCTION public.validate_return_transfer() OWNER TO nexoruta_schema_owner;
        REVOKE ALL ON FUNCTION public.validate_return_transfer() FROM PUBLIC;
        CREATE TRIGGER return_transfer_validate BEFORE INSERT ON public."TransferenciasDevolucion"
            FOR EACH ROW EXECUTE FUNCTION public.validate_return_transfer();
        CREATE TRIGGER return_ack_validate BEFORE INSERT ON public."AcusesDevolucionComercio"
            FOR EACH ROW EXECUTE FUNCTION public.validate_return_transfer();

        CREATE FUNCTION public.validate_return_transfer_parcel() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
        DECLARE v_parcel public."Bultos";
        BEGIN
            SELECT * INTO v_parcel FROM public."Bultos" b WHERE b."Id"=NEW."BultoId" AND b."OperadorId"=NEW."OperadorId" AND b."EnvioId"=NEW."EnvioId";
            IF NOT FOUND OR v_parcel."Version"<>NEW."VersionNueva" OR v_parcel."Custodia"<>'Comercio'
                OR v_parcel."Resultado"<>'Devuelto' THEN
                RAISE EXCEPTION 'Return transfer line does not match canonical parcel state' USING ERRCODE='23514';
            END IF;
            RETURN NEW;
        END $$;
        ALTER FUNCTION public.validate_return_transfer_parcel() OWNER TO nexoruta_schema_owner;
        REVOKE ALL ON FUNCTION public.validate_return_transfer_parcel() FROM PUBLIC;
        CREATE TRIGGER return_transfer_parcel_validate BEFORE INSERT ON public."TransferenciasDevolucionBultos"
            FOR EACH ROW EXECUTE FUNCTION public.validate_return_transfer_parcel();

        CREATE FUNCTION public.validate_return_ack_parcel() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
        DECLARE v_transfer_line public."TransferenciasDevolucionBultos"; v_ack public."AcusesDevolucionComercio";
        BEGIN
            SELECT * INTO v_transfer_line FROM public."TransferenciasDevolucionBultos" l
                WHERE l."TransferenciaId"=NEW."TransferenciaId" AND l."BultoId"=NEW."BultoId";
            SELECT * INTO v_ack FROM public."AcusesDevolucionComercio" a WHERE a."Id"=NEW."AcuseId";
            IF v_ack."Id" IS NULL OR v_transfer_line."BultoId" IS NULL
                OR v_transfer_line."OperadorId"<>NEW."OperadorId" OR v_transfer_line."ComercioId"<>NEW."ComercioId"
                OR v_transfer_line."EnvioId"<>NEW."EnvioId" OR v_transfer_line."VersionNueva"<>NEW."VersionBulto"
                OR (v_ack."Resultado"='Conforme' AND NEW."Observacion" IS NOT NULL)
                OR (v_ack."Resultado"='ConObservaciones' AND (NEW."Observacion" IS NULL OR length(btrim(NEW."Observacion")) NOT BETWEEN 10 AND 500)) THEN
                RAISE EXCEPTION 'Return acknowledgement line does not match transfer' USING ERRCODE='23514';
            END IF;
            RETURN NEW;
        END $$;
        ALTER FUNCTION public.validate_return_ack_parcel() OWNER TO nexoruta_schema_owner;
        REVOKE ALL ON FUNCTION public.validate_return_ack_parcel() FROM PUBLIC;
        CREATE TRIGGER return_ack_parcel_validate BEFORE INSERT ON public."AcusesDevolucionBultos"
            FOR EACH ROW EXECUTE FUNCTION public.validate_return_ack_parcel();

        CREATE FUNCTION public.guard_return_history() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
        BEGIN RAISE EXCEPTION 'Return audit history is append-only' USING ERRCODE='55000'; END $$;
        ALTER FUNCTION public.guard_return_history() OWNER TO nexoruta_schema_owner;
        REVOKE ALL ON FUNCTION public.guard_return_history() FROM PUBLIC;
        DO $m$
        DECLARE t text;
        BEGIN
            FOREACH t IN ARRAY ARRAY['TransferenciasDevolucion','TransferenciasDevolucionBultos','AcusesDevolucionComercio','AcusesDevolucionBultos'] LOOP
                EXECUTE format('CREATE TRIGGER return_audit_immutable BEFORE UPDATE OR DELETE ON public.%I FOR EACH ROW EXECUTE FUNCTION public.guard_return_history()', t);
                EXECUTE format('CREATE TRIGGER return_audit_no_truncate BEFORE TRUNCATE ON public.%I FOR EACH STATEMENT EXECUTE FUNCTION public.guard_return_history()', t);
            END LOOP;
        END $m$;

        CREATE FUNCTION public.require_return_lines() RETURNS trigger LANGUAGE plpgsql SET search_path=pg_catalog AS $$
        DECLARE v_count integer;
        BEGIN
            IF TG_TABLE_NAME='TransferenciasDevolucion' THEN
                SELECT count(*) INTO v_count FROM public."TransferenciasDevolucionBultos" l WHERE l."TransferenciaId"=NEW."Id";
            ELSE
                SELECT count(*) INTO v_count FROM public."AcusesDevolucionBultos" l WHERE l."AcuseId"=NEW."Id";
            END IF;
            IF v_count < 1 OR v_count > 100 THEN RAISE EXCEPTION 'Return commands require 1 to 100 parcel lines' USING ERRCODE='23514'; END IF;
            RETURN NULL;
        END $$;
        ALTER FUNCTION public.require_return_lines() OWNER TO nexoruta_schema_owner;
        REVOKE ALL ON FUNCTION public.require_return_lines() FROM PUBLIC;
        CREATE CONSTRAINT TRIGGER return_transfer_lines_required AFTER INSERT ON public."TransferenciasDevolucion"
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.require_return_lines();
        CREATE CONSTRAINT TRIGGER return_ack_lines_required AFTER INSERT ON public."AcusesDevolucionComercio"
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.require_return_lines();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("AP-04 contains immutable physical and commercial audit facts.");
}
