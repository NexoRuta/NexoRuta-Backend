using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Application.Seguimiento.UseCases;
using NexoRuta.Api.Features.IdentidadVisual.Contracts;
using NexoRuta.Api.Features.Seguimiento.Contracts;
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
    [ProducesResponseType<SeguimientoEstadoResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> ConsultarAsync(
        string token,
        CancellationToken cancellationToken)
    {
        Response.Headers["Cache-Control"] = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";

        var seguimiento = await consultarSeguimiento.EjecutarConIdentidadAsync(
            token,
            cancellationToken);

        if (seguimiento is null)
            return NotFound();

        return Ok(new SeguimientoEstadoResponse(
            seguimiento.Estado.ToString(),
            new IdentidadVisualPublicaResponse(
                seguimiento.Identidad.Nombre,
                seguimiento.Identidad.CorreoPublico,
                seguimiento.Identidad.TelefonoPublico,
                seguimiento.Identidad.ColorPrimario,
                seguimiento.Identidad.ColorSecundario,
                seguimiento.Identidad.LogoContenido,
                seguimiento.Identidad.LogoTipoContenido)));
    }
}
