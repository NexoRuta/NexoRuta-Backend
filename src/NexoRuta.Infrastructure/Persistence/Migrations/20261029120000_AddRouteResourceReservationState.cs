using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Persistence.Migrations;

[DbContext(typeof(NexoRutaDbContext))]
[Migration("20261029120000_AddRouteResourceReservationState")]
public sealed class AddRouteResourceReservationState : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE public."HojasRuta"
            ADD COLUMN "ReservaRecursosActiva" boolean NOT NULL DEFAULT true;

        -- Empty planned routes retain their reservation. Only finalized routes release resources here.
        UPDATE public."HojasRuta"
        SET "ReservaRecursosActiva" = false
        WHERE "Estado" = 'Finalizada';

        DROP INDEX public."IX_HojasRuta_OperadorId_RepartidorAccesoId_Fecha";
        DROP INDEX public."IX_HojasRuta_OperadorId_VehiculoId_Fecha";
        CREATE UNIQUE INDEX "IX_HojasRuta_OperadorId_RepartidorAccesoId_Fecha"
            ON public."HojasRuta" ("OperadorId", "RepartidorAccesoId", "Fecha")
            WHERE "Estado" NOT IN ('Finalizada', 'Cancelada') AND "ReservaRecursosActiva";
        CREATE UNIQUE INDEX "IX_HojasRuta_OperadorId_VehiculoId_Fecha"
            ON public."HojasRuta" ("OperadorId", "VehiculoId", "Fecha")
            WHERE "Estado" NOT IN ('Finalizada', 'Cancelada') AND "ReservaRecursosActiva";
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $rollback$ BEGIN
            IF EXISTS (SELECT 1 FROM public."HojasRuta" WHERE "Estado" <> 'Finalizada'
                GROUP BY "OperadorId", "RepartidorAccesoId", "Fecha" HAVING count(*) > 1)
                OR EXISTS (SELECT 1 FROM public."HojasRuta" WHERE "Estado" <> 'Finalizada'
                    GROUP BY "OperadorId", "VehiculoId", "Fecha" HAVING count(*) > 1) THEN
                RAISE EXCEPTION 'Cannot restore route resource uniqueness while multiple pending routes share a resource and date.';
            END IF;
        END $rollback$;

        DROP INDEX public."IX_HojasRuta_OperadorId_RepartidorAccesoId_Fecha";
        DROP INDEX public."IX_HojasRuta_OperadorId_VehiculoId_Fecha";
        ALTER TABLE public."HojasRuta" DROP COLUMN "ReservaRecursosActiva";
        CREATE UNIQUE INDEX "IX_HojasRuta_OperadorId_RepartidorAccesoId_Fecha"
            ON public."HojasRuta" ("OperadorId", "RepartidorAccesoId", "Fecha") WHERE "Estado" <> 'Finalizada';
        CREATE UNIQUE INDEX "IX_HojasRuta_OperadorId_VehiculoId_Fecha"
            ON public."HojasRuta" ("OperadorId", "VehiculoId", "Fecha") WHERE "Estado" <> 'Finalizada';
        """);
}
