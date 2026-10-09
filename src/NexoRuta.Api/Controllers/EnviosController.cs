using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Api.Contracts.Envios;
using NexoRuta.Application.Administracion;
using NexoRuta.Application.Envios;
using NexoRuta.Domain.Administracion;
using EnvioCreado = NexoRuta.Api.Contracts.Envios.EnvioCreado;
using EnvioDetalle = NexoRuta.Api.Contracts.Envios.EnvioDetalle;

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
                OperadorId: request.OperadorId!.Value,
                DestinatarioNombre: request.DestinatarioNombre,
                Direccion: request.Direccion,
                CodigoBulto: request.CodigoBulto,
                PesoGramos: request.PesoGramos,
                LargoCentimetros: request.LargoCentimetros,
                AnchoCentimetros: request.AnchoCentimetros,
                AltoCentimetros: request.AltoCentimetros), cancellationToken);

            return Created("/api/envios", EnvioCreado.Desde(created));
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
            var envios = await listarEnvios.EjecutarAsync(cancellationToken);
            return Ok(envios.Select(EnvioDetalle.Desde).ToArray());
        }
        catch (AccesoActualNoDisponibleException exception)
        {
            return Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
