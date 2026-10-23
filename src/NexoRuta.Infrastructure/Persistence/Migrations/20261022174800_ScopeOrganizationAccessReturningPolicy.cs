using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Persistence.Migrations;

[DbContext(typeof(NexoRutaDbContext))]
[Migration("20261022174800_ScopeOrganizationAccessReturningPolicy")]
public sealed class ScopeOrganizationAccessReturningPolicy : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP POLICY user_access_api_select ON public."AccesosUsuario";
        CREATE POLICY user_access_api_select ON public."AccesosUsuario" FOR SELECT TO nexoruta_api
            USING (((nexoruta_app.setting('actor_kind') IN ('comercio','operador') AND
                      "TipoOrganizacion" = CASE WHEN nexoruta_app.setting('actor_kind') = 'comercio' THEN 'Comercio' ELSE 'Operador' END AND
                      "OrganizacionId" = nexoruta_app.ctx_uuid('org_id')))
                OR (nexoruta_app.setting('actor_kind') = 'identidad' AND "UsuarioId" = nexoruta_app.ctx_uuid('user_id'))
                OR (nexoruta_app.setting('actor_kind') = 'suspendida' AND "UsuarioId" = nexoruta_app.ctx_uuid('user_id')
                    AND "OrganizacionId" = nexoruta_app.ctx_uuid('org_id'))
                OR (nexoruta_app.setting('actor_kind') = 'bootstrap' AND "UsuarioId" = nexoruta_app.ctx_uuid('user_id')
                    AND (("OperadorId" IS NOT NULL AND "OperadorId" = nexoruta_app.ctx_uuid('bootstrap_operator_id'))
                      OR ("ComercioId" IS NOT NULL AND "ComercioId" = nexoruta_app.ctx_uuid('bootstrap_commerce_id'))))
                OR (nexoruta_app.setting('actor_kind') = 'identidad' AND
                    "UsuarioId" = nexoruta_app.ctx_uuid('target_user_id') AND "RolId" = nexoruta_app.ctx_uuid('target_role_id')
                    AND "OrganizacionId" = nexoruta_app.ctx_uuid('selected_org_id') AND "EsPropietario")
                OR (nexoruta_app.setting('actor_kind') = 'plataforma' AND nexoruta_app.ctx_bool('platform_manage_organizations') AND
                    "UsuarioId" = nexoruta_app.ctx_uuid('target_user_id') AND "RolId" = nexoruta_app.ctx_uuid('target_role_id')
                    AND "OrganizacionId" = nexoruta_app.ctx_uuid('selected_org_id') AND "EsPropietario"));

        DROP POLICY user_access_api_insert ON public."AccesosUsuario";
        CREATE POLICY user_access_api_insert ON public."AccesosUsuario" FOR INSERT TO nexoruta_api
            WITH CHECK (((nexoruta_app.setting('actor_kind') IN ('comercio','operador') AND
                      "TipoOrganizacion" = CASE WHEN nexoruta_app.setting('actor_kind') = 'comercio' THEN 'Comercio' ELSE 'Operador' END AND
                      "OrganizacionId" = nexoruta_app.ctx_uuid('org_id') AND NOT "EsPropietario")
                OR (nexoruta_app.setting('actor_kind') = 'identidad' AND
                    "UsuarioId" = nexoruta_app.ctx_uuid('target_user_id') AND "RolId" = nexoruta_app.ctx_uuid('target_role_id')
                    AND "OrganizacionId" = nexoruta_app.ctx_uuid('selected_org_id') AND "EsPropietario")
                OR (nexoruta_app.setting('actor_kind') = 'plataforma' AND nexoruta_app.ctx_bool('platform_manage_organizations') AND
                    "UsuarioId" = nexoruta_app.ctx_uuid('target_user_id') AND "RolId" = nexoruta_app.ctx_uuid('target_role_id')
                    AND "OrganizacionId" = nexoruta_app.ctx_uuid('selected_org_id') AND "EsPropietario")
                OR (nexoruta_app.setting('actor_kind') = 'bootstrap' AND "UsuarioId" = nexoruta_app.ctx_uuid('user_id')
                    AND "EsPropietario" AND (("OperadorId" IS NOT NULL AND "OperadorId" = nexoruta_app.ctx_uuid('bootstrap_operator_id'))
                      OR ("ComercioId" IS NOT NULL AND "ComercioId" = nexoruta_app.ctx_uuid('bootstrap_commerce_id'))))));
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP POLICY user_access_api_select ON public."AccesosUsuario";
        CREATE POLICY user_access_api_select ON public."AccesosUsuario" FOR SELECT TO nexoruta_api
            USING (((nexoruta_app.setting('actor_kind') IN ('comercio','operador') AND
                      "TipoOrganizacion" = CASE WHEN nexoruta_app.setting('actor_kind') = 'comercio' THEN 'Comercio' ELSE 'Operador' END AND
                      "OrganizacionId" = nexoruta_app.ctx_uuid('org_id')))
                OR (nexoruta_app.setting('actor_kind') = 'identidad' AND "UsuarioId" = nexoruta_app.ctx_uuid('user_id'))
                OR (nexoruta_app.setting('actor_kind') = 'suspendida' AND "UsuarioId" = nexoruta_app.ctx_uuid('user_id')
                    AND "OrganizacionId" = nexoruta_app.ctx_uuid('org_id'))
                OR (nexoruta_app.setting('actor_kind') = 'bootstrap' AND "UsuarioId" = nexoruta_app.ctx_uuid('user_id')
                    AND (("OperadorId" IS NOT NULL AND "OperadorId" = nexoruta_app.ctx_uuid('bootstrap_operator_id'))
                      OR ("ComercioId" IS NOT NULL AND "ComercioId" = nexoruta_app.ctx_uuid('bootstrap_commerce_id')))));

        DROP POLICY user_access_api_insert ON public."AccesosUsuario";
        CREATE POLICY user_access_api_insert ON public."AccesosUsuario" FOR INSERT TO nexoruta_api
            WITH CHECK (((nexoruta_app.setting('actor_kind') IN ('comercio','operador') AND
                      "TipoOrganizacion" = CASE WHEN nexoruta_app.setting('actor_kind') = 'comercio' THEN 'Comercio' ELSE 'Operador' END AND
                      "OrganizacionId" = nexoruta_app.ctx_uuid('org_id') AND NOT "EsPropietario")
                OR (nexoruta_app.setting('actor_kind') IN ('identidad','plataforma') AND
                    "UsuarioId" = nexoruta_app.ctx_uuid('target_user_id') AND "RolId" = nexoruta_app.ctx_uuid('target_role_id')
                    AND "OrganizacionId" = nexoruta_app.ctx_uuid('selected_org_id') AND "EsPropietario")
                OR (nexoruta_app.setting('actor_kind') = 'bootstrap' AND "UsuarioId" = nexoruta_app.ctx_uuid('user_id')
                    AND "EsPropietario" AND (("OperadorId" IS NOT NULL AND "OperadorId" = nexoruta_app.ctx_uuid('bootstrap_operator_id'))
                      OR ("ComercioId" IS NOT NULL AND "ComercioId" = nexoruta_app.ctx_uuid('bootstrap_commerce_id'))))));
        """);
}
