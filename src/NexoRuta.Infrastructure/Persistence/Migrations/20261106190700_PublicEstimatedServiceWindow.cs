using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace NexoRuta.Infrastructure.Persistence.Migrations;

[DbContext(typeof(NexoRutaDbContext))]
[Migration("20261106190700_PublicEstimatedServiceWindow")]
public sealed class PublicEstimatedServiceWindow : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
GRANT SELECT ON public."OperacionProyeccion",public."OperacionFuente",public."OperacionProyeccionSalud" TO nexoruta_public_fn;
CREATE POLICY tracking_projected_stop ON public."OperacionProyeccion" FOR SELECT TO nexoruta_public_fn USING("Tipo"='Parada' AND EXISTS(
 SELECT 1 FROM public."Envios" e WHERE e."OperadorId"="OperacionProyeccion"."OperadorId" AND e."Id"="OperacionProyeccion"."EnvioId"));
CREATE POLICY tracking_projection_frontier ON public."OperacionFuente" FOR SELECT TO nexoruta_public_fn USING(EXISTS(
 SELECT 1 FROM public."Envios" e WHERE e."OperadorId"="OperacionFuente"."OperadorId"));
CREATE FUNCTION nexoruta_app.public_estimated_service_window(p_token text)
 RETURNS TABLE("DesdeUtc" timestamptz,"HastaUtc" timestamptz,"Origen" text,"ActualizadaEnUtc" timestamptz)
 LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
BEGIN
 IF p_token IS NULL OR p_token !~ '^[0-9a-f]{64}$' THEN RETURN; END IF;
 PERFORM set_config('nexoruta.public_token_hash',encode(public.digest(convert_to(p_token,'SQL_ASCII'),'sha256'),'hex'),true);
 IF NOT EXISTS(SELECT 1 FROM public."OperacionProyeccionSalud" WHERE "Proyeccion"='board' GROUP BY "Proyeccion"
 HAVING count(*)=max("Particiones") AND max("Atraso")=0 AND min("VerificadaEnUtc")>clock_timestamp()-interval '15 seconds') THEN RETURN; END IF;
 RETURN QUERY SELECT p."LlegadaEstimadaUtc",p."LlegadaEstimadaUtc"+make_interval(mins=>p."ServicioMinutos"),
 'Planificación manual de atención'::text,projection."RegistradaEnUtc"
 FROM public."ParadasRuta" p JOIN public."HojasRuta" h ON h."Id"=p."RutaId" AND h."OperadorId"=p."OperadorId"
 JOIN public."Envios" e ON e."Id"=p."EnvioId" AND e."OperadorId"=p."OperadorId"
 JOIN public."OperacionFuente" frontier ON frontier."OperadorId"=e."OperadorId" AND frontier."Capturados"=frontier."Proyectados"
 JOIN public."OperacionProyeccion" projection ON projection."OperadorId"=p."OperadorId" AND projection."EnvioId"=p."EnvioId" AND projection."RutaId"=p."RutaId" AND projection."Tipo"='Parada'
 WHERE p."Activa" AND p."ResultadoVisita"='Pendiente' AND p."ServicioMinutos">0 AND p."LlegadaEstimadaUtc">=clock_timestamp()
 AND h."Estado" IN ('Planificada','Despachada','EnCurso') AND e."Estado" IN ('AsignadoRuta','EnTransito','EntregadoParcialmente','RetiroAsignado')
 AND (projection."Evento"->'datos'->>'llegadaEstimadaUtc')::timestamptz=p."LlegadaEstimadaUtc"
 ORDER BY p."LlegadaEstimadaUtc" LIMIT 1;
END $$;
ALTER FUNCTION nexoruta_app.public_estimated_service_window(text) OWNER TO nexoruta_public_fn;
REVOKE ALL ON FUNCTION nexoruta_app.public_estimated_service_window(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION nexoruta_app.public_estimated_service_window(text) TO nexoruta_api;
""");
    protected override void Down(MigrationBuilder m) => throw new NotSupportedException();
}
