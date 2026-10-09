using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Interfaces;

namespace NexoRuta.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/usuarios/actual")]
public sealed class UsuarioActualController(IUsuarioActual usuarioActual) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<ContextoUsuario>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await usuarioActual.ObtenerAsync(cancellationToken));
        }
        catch (AccesoActualNoDisponibleException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
