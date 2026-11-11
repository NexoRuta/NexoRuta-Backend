using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NexoRuta.Infrastructure.Persistence.Migrations;

[DbContext(typeof(NexoRutaDbContext))]
[Migration("20261111010000_PlatformTargetedUserAccessLookup")]
public sealed class PlatformTargetedUserAccessLookup : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE POLICY user_access_platform_target_select ON public."AccesosUsuario" FOR SELECT TO nexoruta_api
            USING (nexoruta_app.setting('actor_kind') = 'plataforma'
                AND nexoruta_app.ctx_bool('platform_manage_organizations')
                AND "UsuarioId" = nexoruta_app.ctx_uuid('target_user_id'));
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP POLICY user_access_platform_target_select ON public."AccesosUsuario";
        """);
}
