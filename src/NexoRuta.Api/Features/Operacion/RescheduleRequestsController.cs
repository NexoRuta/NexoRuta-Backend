using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Reparto.Dtos;
using NexoRuta.Application.Reparto.Exceptions;
using NexoRuta.Application.Reparto.UseCases;
using NexoRuta.Infrastructure.Persistence;
namespace NexoRuta.Api.Features.Operacion;

[ApiController, Route("api/operacion/reprogramaciones"), Authorize(Policy = "envios.consultar"), Authorize(Policy = "envios.gestionar")]
public sealed class RescheduleRequestsController(IUsuarioActual usuario, NexoRutaDbContext db, GestionarRutasUseCase rutas) : ControllerBase
{
    public sealed record Item(Guid Id, Guid EnvioId, long VersionEnvio, DateTimeOffset CreadaEnUtc, string Estado);
    public sealed record Decision(long VersionEnvio, bool Aprobar, string? Motivo);
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var actor = await usuario.ObtenerAsync(ct);
        if (actor.ComercioId is not null || actor.OperadorId is not { } op) return Forbid();
        return Ok(await db.Database.SqlQuery<Item>($"""
            SELECT r."Id",r."EnvioId",e."Version" AS "VersionEnvio",r."CreadaEnUtc",r."Estado"
            FROM public."SolicitudesReprogramacionPublica" r JOIN public."Envios" e ON e."OperadorId"=r."OperadorId" AND e."Id"=r."EnvioId"
            WHERE r."OperadorId"={op} AND r."Estado"='Pendiente' ORDER BY r."CreadaEnUtc",r."Id" LIMIT 200
            """).ToArrayAsync(ct));
    }
    [HttpPost("{id:guid}/resolver")]
    public async Task<IActionResult> Resolve(Guid id, Decision decision, CancellationToken ct)
    {
        var actor = await usuario.ObtenerAsync(ct);
        if (actor.ComercioId is not null || actor.OperadorId is not { } op) return Forbid();
        var item = await db.Database.SqlQuery<Item>($"""
            SELECT "Id","EnvioId","VersionEnvio","CreadaEnUtc","Estado" FROM public."SolicitudesReprogramacionPublica" WHERE "OperadorId"={op} AND "Id"={id} FOR UPDATE
            """).SingleOrDefaultAsync(ct);
        if (item is null) return NotFound();
        if (item.Estado != "Pendiente") return Ok(new { estado = item.Estado });
        if (string.IsNullOrWhiteSpace(decision.Motivo) || decision.Motivo.Length > 500) return BadRequest();
        try
        {
            if (decision.Aprobar && await rutas.ReprogramarAsync(item.EnvioId, new SolicitarReprogramacionEnvioDatos(decision.VersionEnvio) { SolicitudPublicaId = id }, ct) is null) return NotFound();
            var state = decision.Aprobar ? "Tratada" : "Rechazada";
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE public."SolicitudesReprogramacionPublica" SET "Estado"={state},"TratadaEnUtc"=clock_timestamp(),"TratadaPorAccesoId"={actor.AccesoId},"Motivo"={decision.Motivo.Trim()} WHERE "Id"={id} AND "OperadorId"={op}
                """, ct);
            return Ok(new { estado = state });
        }
        catch (ConflictoRepartoException ex) { return Problem(ex.Message, statusCode: 409); }
    }
}
