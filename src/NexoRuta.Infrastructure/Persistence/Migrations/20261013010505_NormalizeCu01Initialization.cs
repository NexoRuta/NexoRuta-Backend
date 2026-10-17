using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeCu01Initialization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(name: "BootstrapCompletado", table: "Operadores", type: "boolean", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<bool>(name: "BootstrapCompletado", table: "Comercios", type: "boolean", nullable: false, defaultValue: false);

            // Existing organizations already have administrative history; bootstrap must not recreate deleted roles.
            migrationBuilder.Sql("UPDATE \"Operadores\" SET \"BootstrapCompletado\" = true; UPDATE \"Comercios\" SET \"BootstrapCompletado\" = true;");
            migrationBuilder.DropForeignKey(
                name: "FK_AccesosUsuario_Roles_RolId",
                table: "AccesosUsuario");

            migrationBuilder.DropIndex(
                name: "IX_Roles_Codigo",
                table: "Roles");

            migrationBuilder.DropIndex(
                name: "IX_AccesosUsuario_RolId",
                table: "AccesosUsuario");

            migrationBuilder.AlterColumn<string>(
                name: "PasswordHash",
                table: "Usuarios",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(1024)",
                oldMaxLength: 1024);

            migrationBuilder.AddColumn<Guid>(
                name: "ComercioId",
                table: "Roles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OperadorId",
                table: "Roles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "RolId",
                table: "AccesosUsuario",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.Sql("ALTER TABLE \"AccesosUsuario\" ADD COLUMN IF NOT EXISTS \"EsPropietario\" boolean;");
            migrationBuilder.Sql("ALTER TABLE \"AccesosUsuario\" ALTER COLUMN \"EsPropietario\" DROP NOT NULL;");
            migrationBuilder.Sql("ALTER TABLE \"Usuarios\" ALTER COLUMN \"PasswordHash\" DROP DEFAULT;");
            migrationBuilder.Sql("ALTER TABLE \"AccesosUsuario\" ALTER COLUMN \"RolId\" DROP DEFAULT;");
            migrationBuilder.Sql("""
                -- Quarantine shared definitions that have no identifiable organization; application never maps these tables.
                CREATE TABLE "LegacyRolesCu01" AS SELECT r.*, EXISTS (SELECT 1 FROM "AccesosUsuario" a WHERE a."RolId" = r."Id") AS "TeniaAccesos" FROM "Roles" r;
                CREATE TABLE "LegacyRolesPermisosCu01" AS TABLE "RolesPermisos";
                CREATE TEMP TABLE role_conversion ON COMMIT DROP AS
                SELECT r."Id" AS old_id,
                       CASE WHEN row_number() OVER (PARTITION BY r."Id" ORDER BY a."OperadorId" NULLS LAST, a."ComercioId") = 1
                            THEN r."Id" ELSE gen_random_uuid() END AS new_id,
                       a."OperadorId", a."ComercioId"
                FROM "Roles" r
                JOIN (SELECT DISTINCT "RolId", "OperadorId", "ComercioId" FROM "AccesosUsuario") a ON a."RolId" = r."Id"
                WHERE (r."TipoOrganizacion" = 'Operador' AND a."OperadorId" IS NOT NULL AND a."ComercioId" IS NULL)
                   OR (r."TipoOrganizacion" = 'Comercio' AND a."ComercioId" IS NOT NULL AND a."OperadorId" IS NULL);
                UPDATE "AccesosUsuario" SET "RolId" = NULL
                  WHERE NOT EXISTS (SELECT 1 FROM role_conversion m WHERE m.old_id = "AccesosUsuario"."RolId"
                    AND m."OperadorId" IS NOT DISTINCT FROM "AccesosUsuario"."OperadorId"
                    AND m."ComercioId" IS NOT DISTINCT FROM "AccesosUsuario"."ComercioId");
                INSERT INTO "Roles" ("Id", "Codigo", "Nombre", "TipoOrganizacion", "OperadorId", "ComercioId")
                  SELECT m.new_id, r."Codigo", r."Nombre", r."TipoOrganizacion", m."OperadorId", m."ComercioId"
                  FROM role_conversion m JOIN "LegacyRolesCu01" r ON r."Id" = m.old_id WHERE m.new_id <> m.old_id;
                UPDATE "Roles" r SET "OperadorId" = m."OperadorId", "ComercioId" = m."ComercioId"
                  FROM role_conversion m WHERE r."Id" = m.new_id;
                INSERT INTO "RolesPermisos" ("RolId", "PermisoId")
                  SELECT m.new_id, p."PermisoId" FROM role_conversion m JOIN "LegacyRolesPermisosCu01" p ON p."RolId" = m.old_id
                  WHERE m.new_id <> m.old_id;
                UPDATE "AccesosUsuario" a SET "RolId" = m.new_id FROM role_conversion m
                  WHERE a."RolId" = m.old_id AND a."OperadorId" IS NOT DISTINCT FROM m."OperadorId"
                    AND a."ComercioId" IS NOT DISTINCT FROM m."ComercioId";
                DELETE FROM "RolesPermisos" WHERE "RolId" IN (SELECT "Id" FROM "Roles" WHERE "OperadorId" IS NULL AND "ComercioId" IS NULL);
                DELETE FROM "Roles" WHERE "OperadorId" IS NULL AND "ComercioId" IS NULL;
                """);

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizacionId",
                table: "Roles",
                type: "uuid",
                nullable: false,
                computedColumnSql: "COALESCE(\"OperadorId\", \"ComercioId\")",
                stored: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OrganizacionId",
                table: "AccesosUsuario",
                type: "uuid",
                nullable: false,
                computedColumnSql: "COALESCE(\"OperadorId\", \"ComercioId\")",
                stored: true);

            migrationBuilder.AddColumn<string>(name: "TipoOrganizacion", table: "AccesosUsuario",
                type: "character varying(20)", maxLength: 20, nullable: false,
                computedColumnSql: "CASE WHEN \"OperadorId\" IS NOT NULL THEN 'Operador' ELSE 'Comercio' END", stored: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Roles_Id_TipoOrganizacion_OrganizacionId",
                table: "Roles",
                columns: new[] { "Id", "TipoOrganizacion", "OrganizacionId" });

            migrationBuilder.CreateIndex(
                name: "IX_Roles_ComercioId",
                table: "Roles",
                column: "ComercioId");

            migrationBuilder.CreateIndex(
                name: "IX_Roles_OperadorId",
                table: "Roles",
                column: "OperadorId");

            migrationBuilder.CreateIndex(
                name: "IX_Roles_TipoOrganizacion_OrganizacionId_Codigo",
                table: "Roles",
                columns: new[] { "TipoOrganizacion", "OrganizacionId", "Codigo" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Roles_Pertenencia",
                table: "Roles",
                sql: "((\"OperadorId\" IS NOT NULL AND \"ComercioId\" IS NULL AND \"TipoOrganizacion\" = 'Operador') OR (\"ComercioId\" IS NOT NULL AND \"OperadorId\" IS NULL AND \"TipoOrganizacion\" = 'Comercio'))");

            migrationBuilder.CreateIndex(
                name: "IX_AccesosUsuario_RolId_TipoOrganizacion_OrganizacionId",
                table: "AccesosUsuario",
                columns: new[] { "RolId", "TipoOrganizacion", "OrganizacionId" });

            migrationBuilder.AddForeignKey(
                name: "FK_AccesosUsuario_Roles_RolId_TipoOrganizacion_OrganizacionId",
                table: "AccesosUsuario",
                columns: new[] { "RolId", "TipoOrganizacion", "OrganizacionId" },
                principalTable: "Roles",
                principalColumns: new[] { "Id", "TipoOrganizacion", "OrganizacionId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Roles_Comercios_ComercioId",
                table: "Roles",
                column: "ComercioId",
                principalTable: "Comercios",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Roles_Operadores_OperadorId",
                table: "Roles",
                column: "OperadorId",
                principalTable: "Operadores",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Refuse ambiguous rollback rather than merge grants or manufacture role codes.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                  IF EXISTS (SELECT 1 FROM "Roles" GROUP BY "Codigo" HAVING count(*) > 1) THEN
                    RAISE EXCEPTION 'Rollback requires explicit consolidation of organization-owned role codes; no role or permission was changed';
                  END IF;
                END $$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_AccesosUsuario_Roles_RolId_TipoOrganizacion_OrganizacionId",
                table: "AccesosUsuario");

            migrationBuilder.DropForeignKey(
                name: "FK_Roles_Comercios_ComercioId",
                table: "Roles");

            migrationBuilder.DropForeignKey(
                name: "FK_Roles_Operadores_OperadorId",
                table: "Roles");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Roles_Id_TipoOrganizacion_OrganizacionId",
                table: "Roles");

            migrationBuilder.DropIndex(
                name: "IX_Roles_ComercioId",
                table: "Roles");

            migrationBuilder.DropIndex(
                name: "IX_Roles_OperadorId",
                table: "Roles");

            migrationBuilder.DropIndex(
                name: "IX_Roles_TipoOrganizacion_OrganizacionId_Codigo",
                table: "Roles");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Roles_Pertenencia",
                table: "Roles");

            migrationBuilder.DropIndex(
                name: "IX_AccesosUsuario_RolId_TipoOrganizacion_OrganizacionId",
                table: "AccesosUsuario");

            migrationBuilder.DropColumn(name: "BootstrapCompletado", table: "Operadores");
            migrationBuilder.DropColumn(name: "BootstrapCompletado", table: "Comercios");
            migrationBuilder.DropColumn(name: "TipoOrganizacion", table: "AccesosUsuario");

            migrationBuilder.DropColumn(
                name: "OrganizacionId",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "OrganizacionId",
                table: "AccesosUsuario");

            migrationBuilder.DropColumn(
                name: "ComercioId",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "OperadorId",
                table: "Roles");

            migrationBuilder.Sql("""
                INSERT INTO "Roles" ("Id", "Codigo", "Nombre", "TipoOrganizacion")
                  SELECT legacy."Id", legacy."Codigo", legacy."Nombre", legacy."TipoOrganizacion"
                  FROM "LegacyRolesCu01" legacy WHERE NOT EXISTS (SELECT 1 FROM "Roles" r WHERE r."Id" = legacy."Id");
                INSERT INTO "RolesPermisos" ("RolId", "PermisoId")
                  SELECT rp."RolId", rp."PermisoId" FROM "LegacyRolesPermisosCu01" rp
                  JOIN "LegacyRolesCu01" legacy ON legacy."Id" = rp."RolId"
                  WHERE NOT legacy."TeniaAccesos"
                    AND NOT EXISTS (SELECT 1 FROM "AccesosUsuario" a WHERE a."RolId" = rp."RolId")
                    AND EXISTS (SELECT 1 FROM "Permisos" p WHERE p."Id" = rp."PermisoId")
                  ON CONFLICT DO NOTHING;
                DROP TABLE "LegacyRolesPermisosCu01";
                DROP TABLE "LegacyRolesCu01";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Codigo",
                table: "Roles",
                column: "Codigo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccesosUsuario_RolId",
                table: "AccesosUsuario",
                column: "RolId");

            migrationBuilder.AddForeignKey(
                name: "FK_AccesosUsuario_Roles_RolId",
                table: "AccesosUsuario",
                column: "RolId",
                principalTable: "Roles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
