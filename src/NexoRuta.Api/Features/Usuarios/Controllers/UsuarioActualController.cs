using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.UseCases;

namespace NexoRuta.Api.Features.Usuarios.Controllers;

[ApiController]
[Authorize]
[Route("api/usuarios/actual")]
public sealed class UsuarioActualController(
    IUsuarioActual usuarioActual,
    ObtenerPermisosActualesUseCase permisosActuales) : ControllerBase
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

    [HttpGet("permisos")]
    [ProducesResponseType<IReadOnlyList<string>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<string>>> GetPermissions(CancellationToken ct)
        => Ok(await permisosActuales.EjecutarAsync(ct));
}
