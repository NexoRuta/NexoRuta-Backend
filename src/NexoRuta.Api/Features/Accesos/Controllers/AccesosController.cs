using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Domain.Administracion;

namespace NexoRuta.Api.Features.Accesos.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/accesos")]
public sealed class AccesosController(IAccesosUsuarioRepository accesos) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ContextoUsuario>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Listar([FromQuery] TipoAccesoUsuario tipo, CancellationToken cancellationToken)
        => Enum.IsDefined(tipo)
            ? Ok(await accesos.ListarAsync(tipo, cancellationToken))
            : BadRequest(new { message = "El tipo de acceso debe ser Operador o Comercio." });
}
