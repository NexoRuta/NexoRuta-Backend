using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NexoRuta.Infrastructure.Persistence.Migrations;

[DbContext(typeof(NexoRutaDbContext))]
[Migration("20261105180000_IndependentParcelProofDeadlines")]
public sealed class IndependentParcelProofDeadlines : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
ALTER POLICY shipment_proof_deadline_fn_update ON public."Envios"
 USING(nexoruta_app.setting('actor_kind')='worker' AND nexoruta_app.setting('history_process')='worker:proof-validation-deadline'
  AND "OperadorId"=nexoruta_app.ctx_uuid('org_id') AND "Id"=nexoruta_app.ctx_uuid('job_shipment_id') AND "Estado" IN ('EntregaPendienteValidacion','EntregadoParcialmente','EntregaSinValidacion'))
 WITH CHECK("Estado" IN ('EntregaSinValidacion','EntregadoParcialmente'));
CREATE OR REPLACE FUNCTION nexoruta_app.expire_pending_proof_deadlines(p_now timestamptz,p_batch_size integer)
 RETURNS integer LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog AS $$
DECLARE c record; n int; total int:=0;
BEGIN
 IF p_now IS NULL OR p_batch_size NOT BETWEEN 1 AND 500 THEN RAISE EXCEPTION 'Invalid deadline sweep input' USING ERRCODE='22023'; END IF;
 PERFORM set_config('nexoruta.actor_kind','worker',true);
 PERFORM set_config('nexoruta.history_process','worker:proof-validation-deadline:scan',true);
 FOR c IN SELECT e."OperadorId",e."Id" FROM public."Envios" e WHERE e."Estado" IN ('EntregaPendienteValidacion','EntregadoParcialmente','EntregaSinValidacion')
   AND (EXISTS(SELECT 1 FROM public."Bultos" b WHERE b."EnvioId"=e."Id" AND b."OperadorId"=e."OperadorId" AND b."Resultado"='EntregaPendienteValidacion' AND b."PruebaVenceEnUtc"<=p_now)
     OR e."Estado"='EntregaPendienteValidacion' AND e."PlazoPruebaVenceEnUtc"<=p_now AND NOT EXISTS(SELECT 1 FROM public."Bultos" b WHERE b."EnvioId"=e."Id" AND b."Resultado"<>'Pendiente'))
   ORDER BY e."Id" LIMIT p_batch_size
 LOOP
  PERFORM set_config('nexoruta.history_process','worker:proof-validation-deadline',true);
  PERFORM set_config('nexoruta.org_id',c."OperadorId"::text,true); PERFORM set_config('nexoruta.job_shipment_id',c."Id"::text,true);
  PERFORM set_config('nexoruta.history_source_type','PlazoValidacionPrueba',true); PERFORM set_config('nexoruta.history_source_id',c."Id"::text,true);
  PERFORM set_config('nexoruta.history_correlation_id',c."Id"::text,true); PERFORM set_config('nexoruta.history_operation_id','',true);
  PERFORM set_config('nexoruta.history_occurred_at',p_now::text,true); PERFORM set_config('nexoruta.history_fact_code','PruebaBultoVencida',true);
  PERFORM 1 FROM public."Envios" WHERE "OperadorId"=c."OperadorId" AND "Id"=c."Id" FOR UPDATE;
  UPDATE public."Bultos" SET "Resultado"='EntregaSinValidacion',"Version"="Version"+1 WHERE "OperadorId"=c."OperadorId" AND "EnvioId"=c."Id" AND "Resultado"='EntregaPendienteValidacion' AND "PruebaVenceEnUtc"<=p_now;
  GET DIAGNOSTICS n=ROW_COUNT;
  -- Candidates are selected before waiting on the row lock. Another sweep may
  -- have resolved them meanwhile; a replay must not advance the shipment again.
  IF n>0 OR EXISTS(SELECT 1 FROM public."Envios" e WHERE e."OperadorId"=c."OperadorId" AND e."Id"=c."Id"
     AND e."Estado"='EntregaPendienteValidacion' AND e."PlazoPruebaVenceEnUtc"<=p_now
     AND NOT EXISTS(SELECT 1 FROM public."Bultos" b WHERE b."OperadorId"=c."OperadorId" AND b."EnvioId"=c."Id" AND b."Resultado"<>'Pendiente')) THEN
   UPDATE public."Envios" SET "Estado"=CASE WHEN "Estado"='EntregadoParcialmente' THEN "Estado" ELSE 'EntregaSinValidacion' END,"Version"="Version"+1
    WHERE "OperadorId"=c."OperadorId" AND "Id"=c."Id" AND "Estado" IN ('EntregaPendienteValidacion','EntregadoParcialmente','EntregaSinValidacion');
   GET DIAGNOSTICS n=ROW_COUNT; total:=total+n;
  END IF;
  PERFORM set_config('nexoruta.history_process','worker:proof-validation-deadline:scan',true);
 END LOOP;
 RETURN total;
END $$;

""");
    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException("Parcel deadline resolution must not be rolled back over accepted physical results.");
}
