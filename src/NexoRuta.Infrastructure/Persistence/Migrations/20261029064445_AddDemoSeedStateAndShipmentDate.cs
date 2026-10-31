using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDemoSeedStateAndShipmentDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreadoEnUtc",
                table: "Envios",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.CreateTable(
                name: "EjecucionSeedDemo",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    SembradoEnUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Operadores = table.Column<int>(type: "integer", nullable: false),
                    Comercios = table.Column<int>(type: "integer", nullable: false),
                    CuentasOperador = table.Column<int>(type: "integer", nullable: false),
                    CuentasComercio = table.Column<int>(type: "integer", nullable: false),
                    Envios = table.Column<int>(type: "integer", nullable: false),
                    Rutas = table.Column<int>(type: "integer", nullable: false),
                    Operaciones = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EjecucionSeedDemo", x => x.Id);
                    table.CheckConstraint("CK_EjecucionSeedDemo_IdUnico", "\"Id\" = 1");
                });

            migrationBuilder.Sql("""
                REVOKE ALL ON public."EjecucionSeedDemo" FROM PUBLIC;
                GRANT SELECT, INSERT ON public."EjecucionSeedDemo" TO nexoruta_api;
                ALTER TABLE public."EjecucionSeedDemo" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."EjecucionSeedDemo" FORCE ROW LEVEL SECURITY;
                CREATE POLICY demo_seed_platform_read ON public."EjecucionSeedDemo"
                    FOR SELECT TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind') = 'plataforma'
                        AND nexoruta_app.ctx_bool('platform_manage_organizations'));
                CREATE POLICY demo_seed_platform_insert ON public."EjecucionSeedDemo"
                    FOR INSERT TO nexoruta_api
                    WITH CHECK (nexoruta_app.setting('actor_kind') = 'plataforma'
                        AND nexoruta_app.ctx_bool('platform_manage_organizations'));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP POLICY demo_seed_platform_read ON public."EjecucionSeedDemo";
                DROP POLICY demo_seed_platform_insert ON public."EjecucionSeedDemo";
                ALTER TABLE public."EjecucionSeedDemo" NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."EjecucionSeedDemo" DISABLE ROW LEVEL SECURITY;
                REVOKE ALL ON public."EjecucionSeedDemo" FROM nexoruta_api;
                """);

            migrationBuilder.DropTable(
                name: "EjecucionSeedDemo");

            migrationBuilder.DropColumn(
                name: "CreadoEnUtc",
                table: "Envios");
        }
    }
}
