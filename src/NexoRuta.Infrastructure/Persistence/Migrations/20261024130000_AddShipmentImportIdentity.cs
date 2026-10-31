using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddShipmentImportIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_Envios_ComercioId_Id",
                table: "Envios",
                columns: new[] { "ComercioId", "Id" });

            migrationBuilder.CreateTable(
                name: "IdentidadesEnvioImportado",
                columns: table => new
                {
                    ComercioId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReferenciaExterna = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, collation: "C"),
                    EnvioId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionHuella = table.Column<short>(type: "smallint", nullable: false),
                    Huella = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentidadesEnvioImportado", x => new { x.ComercioId, x.ReferenciaExterna });
                    table.CheckConstraint("CK_IdentidadesEnvioImportado_Huella", "\"VersionHuella\" > 0 AND octet_length(\"Huella\") = 32");
                    table.ForeignKey(
                        name: "FK_IdentidadesEnvioImportado_Envios_ComercioId_EnvioId",
                        columns: x => new { x.ComercioId, x.EnvioId },
                        principalTable: "Envios",
                        principalColumns: new[] { "ComercioId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IdentidadesEnvioImportado_Envios_OperadorId_EnvioId",
                        columns: x => new { x.OperadorId, x.EnvioId },
                        principalTable: "Envios",
                        principalColumns: new[] { "OperadorId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IdentidadesEnvioImportado_ComercioId_EnvioId",
                table: "IdentidadesEnvioImportado",
                columns: new[] { "ComercioId", "EnvioId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdentidadesEnvioImportado_OperadorId_EnvioId",
                table: "IdentidadesEnvioImportado",
                columns: new[] { "OperadorId", "EnvioId" });

            migrationBuilder.Sql("""
                ALTER TABLE public."IdentidadesEnvioImportado" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."IdentidadesEnvioImportado" FORCE ROW LEVEL SECURITY;
                REVOKE ALL ON public."IdentidadesEnvioImportado" FROM PUBLIC, nexoruta_api;
                GRANT SELECT, INSERT ON public."IdentidadesEnvioImportado" TO nexoruta_shipment_fn;
                CREATE POLICY shipment_import_identity_commerce ON public."IdentidadesEnvioImportado" FOR ALL TO nexoruta_shipment_fn
                    USING (nexoruta_app.setting('actor_kind') = 'comercio'
                        AND "ComercioId" = nexoruta_app.ctx_uuid('org_id'))
                    WITH CHECK (nexoruta_app.setting('actor_kind') = 'comercio'
                        AND "ComercioId" = nexoruta_app.ctx_uuid('org_id'));

                CREATE FUNCTION nexoruta_app.lock_import_references(p_references text[]) RETURNS void
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
                AS $lock_import$
                DECLARE v_commerce_id uuid := nexoruta_app.ctx_uuid('org_id');
                        v_user_id uuid := nexoruta_app.ctx_uuid('user_id');
                        v_reference text;
                BEGIN
                    IF nexoruta_app.setting('actor_kind') IS DISTINCT FROM 'comercio' OR v_commerce_id IS NULL
                        OR v_user_id IS NULL OR nexoruta_app.ctx_uuid('access_id') IS NULL
                        OR NOT EXISTS (SELECT 1 FROM public."AccesosUsuario" a WHERE a."Id" = nexoruta_app.ctx_uuid('access_id')
                            AND a."UsuarioId" = v_user_id AND a."ComercioId" = v_commerce_id AND a."Activo" AND a."RolId" IS NOT NULL)
                        OR NOT EXISTS (SELECT 1 FROM public."Comercios" c WHERE c."Id" = v_commerce_id AND c."Estado" = 'Activa') THEN
                        RAISE EXCEPTION 'Import identity lock is not authorized.' USING ERRCODE = '42501';
                    END IF;
                    IF p_references IS NULL OR cardinality(p_references) > 500
                        OR EXISTS (SELECT 1 FROM unnest(p_references) AS refs(value)
                            WHERE value IS NULL OR char_length(value) NOT BETWEEN 1 AND 128 OR value ~ '[[:cntrl:]]') THEN
                        RAISE EXCEPTION 'Import references are invalid.' USING ERRCODE = '22023';
                    END IF;
                    -- Global C-collation order avoids cycles when overlapping batches list references differently.
                    FOR v_reference IN
                        SELECT DISTINCT refs.value COLLATE "C" FROM unnest(p_references) AS refs(value)
                        ORDER BY 1
                    LOOP
                        PERFORM pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(
                            v_commerce_id::text || ':' || char_length(v_reference)::text || ':' || v_reference, 0));
                    END LOOP;
                END
                $lock_import$;
                ALTER FUNCTION nexoruta_app.lock_import_references(text[]) OWNER TO nexoruta_shipment_fn;
                REVOKE ALL ON FUNCTION nexoruta_app.lock_import_references(text[]) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION nexoruta_app.lock_import_references(text[]) TO nexoruta_api;

                CREATE FUNCTION nexoruta_app.find_import_references(p_references text[])
                RETURNS TABLE("ReferenciaExterna" text, "EnvioId" uuid, "OperadorId" uuid, "VersionHuella" smallint, "Huella" bytea)
                LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, nexoruta_app
                AS $find_import$
                DECLARE v_commerce_id uuid := nexoruta_app.ctx_uuid('org_id');
                        v_user_id uuid := nexoruta_app.ctx_uuid('user_id');
                BEGIN
                    IF nexoruta_app.setting('actor_kind') IS DISTINCT FROM 'comercio' OR v_commerce_id IS NULL
                        OR v_user_id IS NULL OR nexoruta_app.ctx_uuid('access_id') IS NULL
                        OR NOT EXISTS (SELECT 1 FROM public."AccesosUsuario" a WHERE a."Id" = nexoruta_app.ctx_uuid('access_id')
                            AND a."UsuarioId" = v_user_id AND a."ComercioId" = v_commerce_id AND a."Activo" AND a."RolId" IS NOT NULL)
                        OR NOT EXISTS (SELECT 1 FROM public."Comercios" c WHERE c."Id" = v_commerce_id AND c."Estado" = 'Activa') THEN
                        RAISE EXCEPTION 'Import identity lookup is not authorized.' USING ERRCODE = '42501';
                    END IF;
                    RETURN QUERY SELECT i."ReferenciaExterna"::text, i."EnvioId", i."OperadorId", i."VersionHuella", i."Huella"
                        FROM public."IdentidadesEnvioImportado" i
                        WHERE i."ComercioId" = v_commerce_id AND i."ReferenciaExterna" = ANY(p_references);
                END
                $find_import$;
                ALTER FUNCTION nexoruta_app.find_import_references(text[]) OWNER TO nexoruta_shipment_fn;
                REVOKE ALL ON FUNCTION nexoruta_app.find_import_references(text[]) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION nexoruta_app.find_import_references(text[]) TO nexoruta_api;

                CREATE FUNCTION nexoruta_app.bind_import_identity(
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP FUNCTION nexoruta_app.bind_import_identity(text, uuid, uuid, smallint, bytea);
                DROP FUNCTION nexoruta_app.find_import_references(text[]);
                DROP FUNCTION nexoruta_app.lock_import_references(text[]);
                """);
            migrationBuilder.DropTable(
                name: "IdentidadesEnvioImportado");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Envios_ComercioId_Id",
                table: "Envios");

        }
    }
}
