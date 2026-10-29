using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Persistence.Migrations;

[DbContext(typeof(NexoRutaDbContext))]
[Migration("20261028010000_EnforceShipmentTransitionMatrix")]
public partial class EnforceShipmentTransitionMatrix : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE FUNCTION public.guard_shipment_state_transition() RETURNS trigger
        LANGUAGE plpgsql SET search_path = pg_catalog AS $$
        BEGIN
            IF NEW."Estado" IS NOT DISTINCT FROM OLD."Estado" THEN
                RETURN NEW;
            END IF;

            IF (OLD."Estado" = 'Admitido' AND NEW."Estado" IN ('EnDeposito', 'Cancelado'))
                OR (OLD."Estado" = 'EnDeposito' AND NEW."Estado" IN ('AsignadoRuta', 'Extraviado', 'Cancelado'))
                OR (OLD."Estado" = 'AsignadoRuta' AND NEW."Estado" IN ('EnTransito', 'Extraviado', 'Cancelado'))
                OR (OLD."Estado" = 'EnTransito' AND NEW."Estado" IN ('Entregado', 'EntregaPendienteValidacion', 'NoEntregado', 'Extraviado', 'Cancelado'))
                OR (OLD."Estado" = 'EntregaPendienteValidacion' AND NEW."Estado" IN ('Entregado', 'EntregaSinValidacion'))
                OR (OLD."Estado" = 'NoEntregado' AND NEW."Estado" IN ('Reprogramado', 'EnDevolucion', 'Cancelado'))
                OR (OLD."Estado" = 'Reprogramado' AND NEW."Estado" IN ('AsignadoRuta', 'Cancelado'))
                OR (OLD."Estado" = 'EnDevolucion' AND NEW."Estado" IN ('Devuelto', 'Extraviado', 'Cancelado')) THEN
                RETURN NEW;
            END IF;

            RAISE EXCEPTION 'Shipment state transition is not allowed (% -> %).', OLD."Estado", NEW."Estado"
                USING ERRCODE = '23514', CONSTRAINT = 'CK_Envios_TransicionEstado';
        END $$;
        ALTER FUNCTION public.guard_shipment_state_transition() OWNER TO nexoruta_schema_owner;
        REVOKE ALL ON FUNCTION public.guard_shipment_state_transition() FROM PUBLIC;
        CREATE TRIGGER shipment_transition_matrix
            BEFORE UPDATE OF "Estado" ON public."Envios"
            FOR EACH ROW EXECUTE FUNCTION public.guard_shipment_state_transition();
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP TRIGGER shipment_transition_matrix ON public."Envios";
        DROP FUNCTION public.guard_shipment_state_transition();
        """);
}
