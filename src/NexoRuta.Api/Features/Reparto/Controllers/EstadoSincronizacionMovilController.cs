using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Api.Features.Reparto.Controllers;

[ApiController, Authorize(Policy = "reparto.ejecutar"), Route("api/mobile/sync-status")]
public sealed class EstadoSincronizacionMovilController(NexoRutaDbContext db, ILogger<EstadoSincronizacionMovilController> logger)
    : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(204), ProducesResponseType(400), ProducesResponseType(403), ProducesResponseType(409)]
    public async Task<IActionResult> Reportar(EstadoSincronizacionMovilRequest request, CancellationToken ct)
    {
        if (request.InstallationId == Guid.Empty || request.Sequence <= 0 || request.PendingOperations is < 0 or > 10_000)
            return BadRequest();
        if (HttpContext.Items[typeof(ContextoUsuario)] is not ContextoUsuario
            { TipoAcceso: TipoAccesoUsuario.Operador, OperadorId: { } operatorId } actor)
            return Forbid();

        var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public."MobileSyncSnapshots"("OperadorId","AccesoId","InstallationId","Sequence","PendingOperations","ReportedAtUtc")
            VALUES ({operatorId},{actor.AccesoId},{request.InstallationId},{request.Sequence},{request.PendingOperations},clock_timestamp())
            ON CONFLICT ("OperadorId","AccesoId","InstallationId") DO UPDATE
            SET "Sequence"=EXCLUDED."Sequence",
                "PendingOperations"=EXCLUDED."PendingOperations",
                "ReportedAtUtc"=clock_timestamp()
            WHERE public."MobileSyncSnapshots"."Sequence" < EXCLUDED."Sequence"
            """, ct);

        if (updated == 0)
        {
            using var scope = logger.BeginScope(new Dictionary<string, object?>
            {
                ["correlation_id"] = request.InstallationId.ToString("D"),
                ["sync_report_sequence"] = request.Sequence
            });
            logger.LogInformation("Stale mobile synchronization status report rejected.");
            return Conflict();
        }

        // Retain disconnected-device visibility for thirty days, then forget installations no longer reporting.
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM public.\"MobileSyncSnapshots\" WHERE \"ReportedAtUtc\" < clock_timestamp() - interval '30 days'", ct);
        return NoContent();
    }
}
