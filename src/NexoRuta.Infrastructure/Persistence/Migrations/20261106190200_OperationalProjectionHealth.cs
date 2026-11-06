using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace NexoRuta.Infrastructure.Persistence.Migrations;

[DbContext(typeof(NexoRutaDbContext))]
[Migration("20261106190200_OperationalProjectionHealth")]
public sealed class OperationalProjectionHealth : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
CREATE TABLE public."OperacionProyeccionSalud"("Proyeccion" text NOT NULL,"Particion" int NOT NULL,"Particiones" int NOT NULL,"Atraso" bigint NOT NULL,"VerificadaEnUtc" timestamptz NOT NULL,PRIMARY KEY("Proyeccion","Particion"));
REVOKE ALL ON public."OperacionProyeccionSalud" FROM PUBLIC;
GRANT SELECT ON public."OperacionProyeccionSalud" TO nexoruta_api;
GRANT SELECT,INSERT,UPDATE ON public."OperacionProyeccionSalud" TO nexoruta_job_fn;
CREATE FUNCTION nexoruta_app.operational_projection_health(p_projection text,p_partition int,p_partitions int,p_lag bigint) RETURNS void
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF p_projection NOT IN ('board','history') OR p_partition<0 OR p_partitions<1 OR p_partition>=p_partitions OR p_lag<0 THEN RAISE EXCEPTION 'Invalid projection health'; END IF;
 INSERT INTO public."OperacionProyeccionSalud" VALUES(p_projection,p_partition,p_partitions,p_lag,clock_timestamp())
 ON CONFLICT("Proyeccion","Particion") DO UPDATE SET "Particiones"=excluded."Particiones","Atraso"=excluded."Atraso","VerificadaEnUtc"=excluded."VerificadaEnUtc";
END $$;
ALTER FUNCTION nexoruta_app.operational_projection_health(text,int,int,bigint) OWNER TO nexoruta_job_fn;
REVOKE ALL ON FUNCTION nexoruta_app.operational_projection_health(text,int,int,bigint) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nexoruta_app.operational_projection_health(text,int,int,bigint) TO nexoruta_worker;
""");
    protected override void Down(MigrationBuilder m) => throw new NotSupportedException();
}
