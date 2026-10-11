using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Infrastructure.Seguimiento.Consultas;

namespace NexoRuta.Api.Features.Seguimiento.Controllers;

[ApiController]
[Authorize(Policy = "seguimiento-demo")]
[Route("api/envios")]
public sealed class EnlaceSeguimientoController(RecuperarEnlaceSeguimiento recuperar) : ControllerBase
{
    [HttpGet("{envioId:guid}/seguimiento")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> RecuperarAsync(Guid envioId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        try
        {
            var enlace = await recuperar.EjecutarAsync(envioId, cancellationToken);
            if (enlace is null) return NotFound();
            if (enlace.Estado == "pendiente")
            {
                Response.Headers.RetryAfter = "2";
                return Accepted(enlace);
            }
            return enlace.Estado == "vigente" ? Ok(enlace) : StatusCode(StatusCodes.Status410Gone, enlace);
        }
        catch (AccesoActualNoDisponibleException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception exception) when (exception is InvalidOperationException or CryptographicException or ArgumentException)
        {
            // Never return ciphertext, keys, tokens, or configuration values in diagnostics.
            return Problem("No se puede recuperar el enlace con la configuración o entrega actual.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
