using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.Infrastructure.Webhooks;
namespace NexoRuta.Api.Features.Webhooks;

public sealed record SaveWebhook(Guid OperadorId, string Url, string[] Eventos, int Contrato, bool Habilitada, long Revision, string? Secreto);
[ApiController, Authorize(Policy="envios.consultar"), Route("api/webhooks")]
public sealed class WebhooksController(NexoRutaDbContext db, WebhookStore store, WebhookSecrets secrets, WebhookDestinationPolicy destinations, IUsuarioActual current) : ControllerBase
{
    public static readonly string[] Events = ["EnvioCreado","OperacionActualizada","BultoActualizado","NuevaVisita","RetiroSolicitado","RetiroRetirado","RetiroEntregadoComercio"];
    [HttpGet("suscripciones")]
    public async Task<IActionResult> Subscriptions(Guid? operadorId, CancellationToken ct)
    {
        Response.Headers.CacheControl="no-store";
        return Ok(await store.Subscriptions.Where(x=>operadorId==null || x.OperadorId==operadorId).OrderBy(x=>x.OperadorId).ThenBy(x=>x.Id).ToArrayAsync(ct));
    }
    [HttpPut("suscripciones/{id:guid}"), Authorize(Policy="configuracion.gestionar")]
    public async Task<IActionResult> Save(Guid id, SaveWebhook command, CancellationToken ct)
    {
        var actor=await current.ObtenerAsync(ct);
        if(actor.ComercioId is not {} commerce) return Forbid();
        if(id==Guid.Empty || command.OperadorId==Guid.Empty || command.Contrato is not (1 or 2) || command.Eventos is not {Length:>0 and <=8} ||
            command.Eventos.Except(Events).Any() || command.Contrato==1 && command.Eventos.Except(Events.Take(2)).Any())
            return Problem("Seleccioná eventos compatibles con la versión del contrato.",statusCode:400);
        try
        {
            var url=destinations.Validate(command.Url).AbsoluteUri;
            var existing=await store.Subscriptions.SingleOrDefaultAsync(x=>x.Id==id,ct);
            if(existing is not null && (existing.OperadorId!=command.OperadorId || existing.ComercioId!=commerce)) return NotFound();
            if(existing is null && command.Revision!=0) return NotFound();
            if(existing is not null && existing.Revision!=command.Revision) return Problem("La suscripción cambió. Recargá antes de guardar.",statusCode:409);
            var protectedSecret=string.IsNullOrWhiteSpace(command.Secreto) ? null : secrets.Protect(command.Secreto);
            if(existing is null && protectedSecret is null) return Problem("Ingresá la clave compartida del receptor.",statusCode:400);
            if(existing?.Revocada==true && command.Habilitada && protectedSecret is null) return Problem("Ingresá una nueva clave para habilitar una suscripción revocada.",statusCode:400);
            var keyId=protectedSecret is null ? existing!.ClaveId : Guid.NewGuid();
            if(existing is null)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO public."WebhookSubscriptions"("Id","OperadorId","ComercioId","Url","Eventos","Contrato","Habilitada","Revision","ClaveId","SecretoProtegido")
                VALUES({id},{command.OperadorId},{commerce},{url},{command.Eventos.Distinct().ToArray()},{command.Contrato},{command.Habilitada},1,{keyId},{protectedSecret})
                """,ct);
            }
            else
            {
                var changed=await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE public."WebhookSubscriptions" SET "Url"={url},"Eventos"={command.Eventos.Distinct().ToArray()},"Contrato"={command.Contrato},"Habilitada"={command.Habilitada},
                "Revision"="Revision"+1,"Revocada"=CASE WHEN {protectedSecret} IS NULL THEN "Revocada" ELSE false END,"ClaveId"={keyId},"SecretoProtegido"=coalesce({protectedSecret},"SecretoProtegido"),"ActualizadaEnUtc"=clock_timestamp()
                WHERE "Id"={id} AND "Revision"={command.Revision}
                """,ct);
                if(changed!=1) return Problem("La suscripción cambió. Recargá antes de guardar.",statusCode:409);
            }
            return Ok(await store.Subscriptions.SingleAsync(x=>x.Id==id,ct));
        }
        catch(ArgumentException ex) { return Problem(ex.Message,statusCode:400); }
    }
    [HttpPost("suscripciones/{id:guid}/revocaciones"), Authorize(Policy="configuracion.gestionar")]
    public async Task<IActionResult> Revoke(Guid id, [FromBody] long revision, CancellationToken ct)
    {
        var actor=await current.ObtenerAsync(ct);if(actor.ComercioId is null)return Forbid();
        if(!await store.Subscriptions.AnyAsync(s=>s.Id==id,ct))return NotFound();
        var revoked=await db.Database.SqlQuery<bool>($"""SELECT nexoruta_app.webhook_revoke({id},{revision}) AS "Value" """).SingleAsync(ct);
        return revoked ? Ok(new {estado="Revocada"}) : Problem("La suscripción cambió. Recargá antes de revocar.",statusCode:409);
    }
    [HttpGet("entregas")]
    public async Task<IActionResult> History(Guid operadorId, string? estado, string? tipo, Guid? suscripcionId, int pagina=1, int tamano=25, CancellationToken ct=default)
    {
        Response.Headers.CacheControl="no-store";
        if(operadorId==Guid.Empty || pagina<1 || pagina>100000 || tamano is <1 or >100) return BadRequest();
        var q=store.Deliveries.Where(x=>x.OperadorId==operadorId && (estado==null || x.Estado==estado) && (tipo==null || x.Tipo==tipo) && (suscripcionId==null || x.SuscripcionId==suscripcionId));
        return Ok(new { total=await q.CountAsync(ct),pagina,tamano,entregas=await q.OrderByDescending(x=>x.CreadaEnUtc).ThenBy(x=>x.Id).Skip((pagina-1)*tamano).Take(tamano).ToArrayAsync(ct) });
    }
    [HttpGet("entregas/{id:guid}")]
    public async Task<IActionResult> Detail(Guid id, int pagina=1, CancellationToken ct=default)
    {
        Response.Headers.CacheControl="no-store";
        if(pagina<1 || pagina>100000) return BadRequest();
        var delivery=await store.Deliveries.SingleOrDefaultAsync(x=>x.Id==id,ct);
        if(delivery is null) return NotFound();
        var attempts=await db.Database.SqlQuery<WebhookAttempt>($"""SELECT "EntregaId","Numero","InicioUtc","FinUtc","CodigoHttp","Diagnostico" FROM public."WebhookAttempts" WHERE "EntregaId"={id}""").OrderByDescending(x=>x.Numero).Skip((pagina-1)*25).Take(25).ToArrayAsync(ct);
        var replays=await db.Database.SqlQuery<WebhookReplay>($"""SELECT "EntregaId","Numero","ActorId","FechaUtc","EstadoAnterior" FROM public."WebhookReplays" WHERE "EntregaId"={id}""").OrderByDescending(x=>x.Numero).Skip((pagina-1)*25).Take(25).ToArrayAsync(ct);
        return Ok(new {entrega=delivery,intentos=attempts,reenvios=replays,pagina});
    }
    [HttpPost("entregas/{id:guid}/reenvios"), Authorize(Policy="configuracion.gestionar")]
    public async Task<IActionResult> Replay(Guid id, CancellationToken ct)
    {
        if(!await store.Deliveries.AnyAsync(x=>x.Id==id,ct)) return NotFound();
        var replay=await db.Database.SqlQuery<bool>($"""SELECT nexoruta_app.webhook_replay({id}) AS "Value" """).SingleAsync(ct);
        return replay ? Ok(new {estado="Pendiente"}) : Problem("Solo se puede reenviar una entrega finalizada.",statusCode:409);
    }
}
