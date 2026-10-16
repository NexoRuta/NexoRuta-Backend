using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.UseCases;
using NexoRuta.Api.Features.Usuarios.Contracts;
using NexoRuta.Domain.Administracion;

namespace NexoRuta.Api.Features.Usuarios.Controllers;

[ApiController]
[Authorize]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[Route("api/usuarios/actual")]
public sealed class UsuarioActualController(
    IUsuarioActual usuarioActual,
    ObtenerPermisosActualesUseCase permisosActuales) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<UsuarioActualResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        try
        {
            if (HttpContext.Items[typeof(ContextoPlataforma)] is ContextoPlataforma plataforma)
                return Ok(UsuarioActualResponse.Desde(plataforma));
            return Ok(UsuarioActualResponse.Desde(await usuarioActual.ObtenerAsync(cancellationToken)));
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
        => HttpContext.Items[typeof(ContextoPlataforma)] is ContextoPlataforma plataforma
            ? Ok(plataforma.PuedeGestionarOrganizaciones ? new[] { RolPlataforma.GestionarOrganizaciones } : [])
            : Ok(await permisosActuales.EjecutarAsync(ct));
}
