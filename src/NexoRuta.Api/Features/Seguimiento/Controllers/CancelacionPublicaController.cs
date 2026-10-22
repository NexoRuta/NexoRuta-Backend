using NexoRuta.Api.Features.Seguimiento.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NexoRuta.Application.Seguimiento.Interfaces;

namespace NexoRuta.Api.Features.Seguimiento.Controllers;

[ApiController, AllowAnonymous, EnableRateLimiting("seguimiento-publico"), Route("api/public/seguimiento/{token}/cancelacion")]
public sealed class CancelacionPublicaController(ICancelacionPublica cancelar) : ControllerBase
{
    [HttpPost("codigo")]
    [ProducesResponseType(202), ProducesResponseType(404), ProducesResponseType(429)]
    public async Task<IActionResult> Solicitar(string token, CancellationToken ct)
    {
        ProtegerRespuesta();
        var resultado = await cancelar.SolicitarCodigoAsync(token, ct);
        if (resultado == "Encolado") return Accepted();
        if (resultado == "Frecuente") { Response.Headers.RetryAfter = "60"; return StatusCode(429, new { message = "Esperá un minuto antes de pedir otro código." }); }
        return NotFound();
    }
    [HttpPost]
    [ProducesResponseType<CancelacionPublicaResponse>(200), ProducesResponseType(400), ProducesResponseType(404), ProducesResponseType(429)]
    public async Task<IActionResult> Confirmar(string token, CodigoCancelacionRequest request, CancellationToken ct)
    {
        ProtegerRespuesta();
        var resultado = await cancelar.ConfirmarAsync(token, request.Codigo, ct);
        return resultado switch
        {
            "Correcto" => Ok(new CancelacionPublicaResponse("Cancelado")),
            "NoDisponible" => NotFound(),
            "Agotado" => BadRequest(new { message = "Se agotaron los cinco intentos. Solicitá un código nuevo." }),
            _ => BadRequest(new { message = "El código no es válido. Revisalo o solicitá uno nuevo." })
        };
    }
    private void ProtegerRespuesta()
    {
        Response.Headers.CacheControl = "no-store"; Response.Headers["Referrer-Policy"] = "no-referrer";
    }
}
