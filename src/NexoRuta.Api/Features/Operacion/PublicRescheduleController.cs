using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Infrastructure.Persistence;
namespace NexoRuta.Api.Features.Operacion;

[ApiController, AllowAnonymous, EnableRateLimiting("seguimiento-publico"), Route("api/public/seguimiento/{token}/reprogramacion")]
public sealed class PublicRescheduleController(NexoRutaDbContext db) : ControllerBase
{
    public sealed record RescheduleRequest(Guid SolicitudId);
    [HttpPost]
    public async Task<IActionResult> RequestAsync(string token, RescheduleRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        var result = await db.Database.SqlQuery<string>($"SELECT nexoruta_app.request_public_reschedule({token},{request.SolicitudId}) AS \"Value\"").SingleAsync(ct);
        return result switch
        {
            "NoDisponible" => NotFound(),
            "NoElegible" => Problem("El envío no admite una solicitud de nueva visita según sus reglas actuales.", statusCode: 409),
            _ => Ok(new { estado = result })
        };
    }
}
