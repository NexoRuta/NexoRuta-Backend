using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NexoRuta.Infrastructure.Persistence.Migrations;

[DbContext(typeof(NexoRutaDbContext))]
[Migration("20261102000000_LimitGeocodingConsumption")]
public sealed class LimitGeocodingConsumption : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE public."UsoGeocodificacion" ("ReservadaEnUtc" timestamptz NOT NULL);
        CREATE INDEX "IX_UsoGeocodificacion_ReservadaEnUtc" ON public."UsoGeocodificacion" ("ReservadaEnUtc");
        ALTER TABLE public."UsoGeocodificacion" OWNER TO nexoruta_schema_owner;
        REVOKE ALL ON public."UsoGeocodificacion" FROM PUBLIC, nexoruta_api, nexoruta_worker;
        CREATE FUNCTION nexoruta_app.reserve_geocoding_request(max_requests integer, rps integer)
        RETURNS integer LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
        DECLARE moment timestamptz;
        BEGIN
          IF max_requests IS NULL OR rps IS NULL OR max_requests NOT BETWEEN 1 AND 2000 OR rps NOT BETWEEN 1 AND 3 THEN
            RAISE EXCEPTION 'Invalid geocoding limits.' USING ERRCODE='22023';
          END IF;
          -- ponytail: one global lock is sufficient for <=3 requests/s; partition only with a shared account budget.
          PERFORM pg_advisory_xact_lock(hashtextextended('nexoruta-geocoding',0));
          moment := clock_timestamp();
          IF (SELECT count(*) FROM public."UsoGeocodificacion" WHERE "ReservadaEnUtc">moment-interval '24 hours')>=max_requests THEN RETURN 1; END IF;
          IF (SELECT count(*) FROM public."UsoGeocodificacion" WHERE "ReservadaEnUtc">moment-interval '1 second')>=rps THEN RETURN 2; END IF;
          DELETE FROM public."UsoGeocodificacion" WHERE "ReservadaEnUtc"<=moment-interval '48 hours';
          INSERT INTO public."UsoGeocodificacion" VALUES(moment);
          RETURN 0;
        END $$;
        ALTER FUNCTION nexoruta_app.reserve_geocoding_request(integer,integer) OWNER TO nexoruta_schema_owner;
        REVOKE ALL ON FUNCTION nexoruta_app.reserve_geocoding_request(integer,integer) FROM PUBLIC, nexoruta_worker;
        GRANT EXECUTE ON FUNCTION nexoruta_app.reserve_geocoding_request(integer,integer) TO nexoruta_api;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP FUNCTION nexoruta_app.reserve_geocoding_request(integer,integer);
        DROP TABLE public."UsoGeocodificacion";
        """);
}
