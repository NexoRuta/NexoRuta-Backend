using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Application.Administracion.Dtos;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Domain.Administracion;

namespace NexoRuta.Api.Features.Comercios.Controllers;

[ApiController]
[Authorize(Policy = nameof(TipoAccesoUsuario.Comercio))]
[Route("api/operadores")]
public sealed class OperadoresDisponiblesController(IOperadoresRepository operadores) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<OperadorDisponible>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Listar(CancellationToken cancellationToken)
    {
        return Ok(await operadores.ListarAsync(cancellationToken));
    }

    [HttpGet("importacion-envios")]
    [Authorize(Policy = "envios.crear")]
    [ProducesResponseType<IReadOnlyList<OperadorDisponibleParaImportacion>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListarParaImportacion(CancellationToken cancellationToken)
        => Ok(await operadores.ListarParaImportacionAsync(cancellationToken));
}
