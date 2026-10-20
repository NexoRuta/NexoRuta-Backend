using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexoRuta.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IntegrateMobileRuntimePermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in TenantTables)
            {
                var previousPolicy = PreviousPolicy(table);
                migrationBuilder.Sql($"""
                    DROP POLICY "{previousPolicy}" ON public."{table}";
                    ALTER TABLE public."{table}" OWNER TO nexoruta_schema_owner;
                    REVOKE ALL ON public."{table}" FROM PUBLIC;
                    GRANT SELECT, INSERT, UPDATE ON public."{table}" TO nexoruta_api;
                    CREATE POLICY mobile_operator ON public."{table}" TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind') = 'operador'
                        AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'))
                    WITH CHECK (nexoruta_app.setting('actor_kind') = 'operador'
                        AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                    """);
            }
            migrationBuilder.Sql("""
                ALTER POLICY permissions_catalog_insert ON public."Permisos"
                    WITH CHECK (nexoruta_app.setting('actor_kind') IN ('plataforma','identidad','bootstrap')
                        AND "Codigo" IN ('usuarios.gestionar','configuracion.gestionar','envios.crear',
                            'envios.consultar','envios.gestionar','plataforma.organizaciones.gestionar','reparto.ejecutar'));
                GRANT SELECT, INSERT, UPDATE ON public."SesionesMoviles", public."TokensRenovacionMoviles" TO nexoruta_api;
                GRANT UPDATE ("Latitud", "Longitud", "ConfirmadaPorUsuarioId", "ConfirmadaEnUtc", "Version")
                    ON public."Direcciones" TO nexoruta_api;
                GRANT UPDATE ("Estado", "EntregaFisicaEnUtc", "Version") ON public."Envios" TO nexoruta_api;
                GRANT UPDATE ("Custodia", "AccesoCustodioId", "RutaCustodiaId", "Version") ON public."Bultos" TO nexoruta_api;
                ALTER POLICY shipment_api_update ON public."Envios"
                    USING (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'))
                    WITH CHECK (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                CREATE POLICY parcel_operator_update ON public."Bultos" FOR UPDATE TO nexoruta_api
                    USING (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'))
                    WITH CHECK (nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER POLICY permissions_catalog_insert ON public."Permisos"
                    WITH CHECK (nexoruta_app.setting('actor_kind') IN ('plataforma','identidad','bootstrap')
                        AND "Codigo" IN ('usuarios.gestionar','configuracion.gestionar','envios.crear',
                            'envios.consultar','envios.gestionar','plataforma.organizaciones.gestionar'));
                DROP POLICY parcel_operator_update ON public."Bultos";
                REVOKE UPDATE ("Custodia", "AccesoCustodioId", "RutaCustodiaId", "Version") ON public."Bultos" FROM nexoruta_api;
                REVOKE UPDATE ("Estado", "EntregaFisicaEnUtc", "Version") ON public."Envios" FROM nexoruta_api;
                REVOKE UPDATE ("Latitud", "Longitud", "ConfirmadaPorUsuarioId", "ConfirmadaEnUtc", "Version")
                    ON public."Direcciones" FROM nexoruta_api;
                REVOKE ALL ON public."SesionesMoviles", public."TokensRenovacionMoviles" FROM nexoruta_api;
                ALTER POLICY shipment_api_update ON public."Envios"
                    USING ((nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'))
                        OR (nexoruta_app.setting('actor_kind') = 'comercio' AND "ComercioId" = nexoruta_app.ctx_uuid('org_id')))
                    WITH CHECK ((nexoruta_app.setting('actor_kind') = 'operador' AND "OperadorId" = nexoruta_app.ctx_uuid('org_id'))
                        OR (nexoruta_app.setting('actor_kind') = 'comercio' AND "ComercioId" = nexoruta_app.ctx_uuid('org_id')));
                """);
            foreach (var table in TenantTables)
            {
                var previousPolicy = PreviousPolicy(table);
                migrationBuilder.Sql($"""
                    DROP POLICY mobile_operator ON public."{table}";
                    REVOKE ALL ON public."{table}" FROM nexoruta_api;
                    ALTER TABLE public."{table}" OWNER TO CURRENT_USER;
                    CREATE POLICY "{previousPolicy}" ON public."{table}"
                    USING ("OperadorId" = NULLIF(current_setting('nexoruta.operador_id', true), '')::uuid)
                    WITH CHECK ("OperadorId" = NULLIF(current_setting('nexoruta.operador_id', true), '')::uuid);
                    """);
            }
        }

        private static string PreviousPolicy(string table) => table switch
        {
            "Repartidores" or "ConfiguracionesReparto" or "Vehiculos" => $"{table}_Operador",
            _ => "operador_reparto"
        };

        private static readonly string[] TenantTables = ["Repartidores", "ConfiguracionesReparto", "Vehiculos",
            "HojasRuta", "ParadasRuta", "OperacionesJornada", "EvidenciasReparto", "PruebasEntrega",
            "AvisosReparto", "InstalacionesMoviles", "EntregasPush"];
    }
}
