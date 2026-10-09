using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Application.Administracion.Dtos;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Domain.Administracion;

namespace NexoRuta.Api.Controllers;

[ApiController]
[Authorize(Policy = nameof(TipoAccesoUsuario.Comercio))]
[Route("api/comercio/operadores")]
public sealed class OperadoresDelComercioController(IAccesosUsuarioRepository cuentas) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<OperadorDisponible>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Listar(CancellationToken cancellationToken)
    {
        return Ok(await cuentas.ListarOperadoresAsync(cancellationToken));
    }
}
