using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PinGuardFunctionSearchPaths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER FUNCTION nexoruta_app.guard_tariff_child_immutability() SET search_path = pg_catalog;
                ALTER FUNCTION nexoruta_app.assert_current_tariff_pointer() SET search_path = pg_catalog;
                ALTER FUNCTION nexoruta_app.assert_published_tariff_is_current() SET search_path = pg_catalog;
                ALTER FUNCTION nexoruta_app.guard_operational_rules_version() SET search_path = pg_catalog;
                ALTER FUNCTION nexoruta_app.guard_operational_rules_children() SET search_path = pg_catalog;
                ALTER FUNCTION nexoruta_app.guard_operational_rules_root() SET search_path = pg_catalog;
                ALTER FUNCTION nexoruta_app.assert_current_operational_rules_publication() SET search_path = pg_catalog;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER FUNCTION nexoruta_app.guard_tariff_child_immutability() SET search_path = pg_catalog, public;
                ALTER FUNCTION nexoruta_app.assert_current_tariff_pointer() SET search_path = pg_catalog, public;
                ALTER FUNCTION nexoruta_app.assert_published_tariff_is_current() SET search_path = pg_catalog, public;
                ALTER FUNCTION nexoruta_app.guard_operational_rules_version() SET search_path = pg_catalog, public;
                ALTER FUNCTION nexoruta_app.guard_operational_rules_children() SET search_path = pg_catalog, public;
                ALTER FUNCTION nexoruta_app.guard_operational_rules_root() SET search_path = pg_catalog, public;
                ALTER FUNCTION nexoruta_app.assert_current_operational_rules_publication() SET search_path = pg_catalog, public;
                """);
        }
    }
}
