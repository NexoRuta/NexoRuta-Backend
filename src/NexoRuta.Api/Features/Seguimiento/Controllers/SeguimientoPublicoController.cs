using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Application.Seguimiento.UseCases;
using Microsoft.AspNetCore.RateLimiting;

namespace NexoRuta.Api.Features.Seguimiento.Controllers;
[ApiController]
[AllowAnonymous] // Permite acceder a este endpoint sin autenticación
[EnableRateLimiting("seguimiento-publico")]
[Route("api/public/seguimiento")]
public sealed class SeguimientoPublicoController(
    ConsultarSeguimientoPublicoUseCase consultarSeguimiento
) : ControllerBase
{
    [HttpGet("{token}")] // => GET /api/public/seguimiento/{token}
    public async Task<IActionResult> ConsultarAsync(
        string token,
        CancellationToken cancellationToken)
    {
        Response.Headers["Cache-Control"] = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";

        var estado = await consultarSeguimiento.EjecutarAsync(
            token,
            cancellationToken);

        if (estado is null)
            return NotFound();

        return Ok(new
        {
            estado = estado.Value.ToString()
        });
    }
}