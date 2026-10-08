using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Api.Contracts.Envios;
using NexoRuta.Application.Administracion;
using NexoRuta.Application.Envios;
using NexoRuta.Domain.Administracion;

namespace NexoRuta.Api.Controllers;

[ApiController]
[Authorize]
[Route("api")]
public sealed class EnviosController(
    CrearEnvioUseCase crearEnvio,
    ListarEnviosUseCase listarEnvios) : ControllerBase
{
    [HttpPost("envios")]
    [Authorize(Policy = nameof(TipoAccesoUsuario.Comercio))]
    [ProducesResponseType<EnvioCreado>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CreateShipment(
        [FromBody] CrearEnvioRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var created = await crearEnvio.EjecutarAsync(new CrearEnvioCommand(
                request.OperadorId!.Value,
                request.DestinatarioNombre,
                request.Direccion,
                request.CodigoBulto,
                request.PesoGramos,
                request.LargoCentimetros,
                request.AnchoCentimetros,
                request.AltoCentimetros), cancellationToken);

            return Created("/api/envios", created);
        }
        catch (OperadorNoDisponibleException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
        catch (AccesoNoPermitidoException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (AccesoActualNoDisponibleException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    [HttpGet("envios")]
    [ProducesResponseType<IReadOnlyList<EnvioDetalle>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetShipments(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await listarEnvios.EjecutarAsync(cancellationToken));
        }
        catch (AccesoActualNoDisponibleException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
