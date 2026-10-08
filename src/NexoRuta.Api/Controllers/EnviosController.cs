using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Application.Envios;

namespace NexoRuta.Api.Controllers;

[ApiController]
[Route("api")]
public sealed class EnviosController(
    CrearEnvioUseCase crearEnvio,
    ObtenerContextoDemoUseCase obtenerContexto,
    ListarEnviosUseCase listarEnvios) : ControllerBase
{
    [HttpGet("demo/context")]
    [ProducesResponseType<DemoComercioContext>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetContext(CancellationToken cancellationToken)
    {
        var context = await obtenerContexto.EjecutarAsync(cancellationToken);
        return context is null ? NotFound(new { message = "No hay datos de demo inicializados." }) : Ok(context);
    }

    [HttpPost("envios")]
    [ProducesResponseType<EnvioCreado>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateShipment(
        [FromBody] CrearEnvioRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var created = await crearEnvio.EjecutarAsync(new CrearEnvioCommand(
                request.DestinatarioNombre,
                request.Direccion,
                request.CodigoBulto,
                request.PesoGramos,
                request.LargoCentimetros,
                request.AnchoCentimetros,
                request.AltoCentimetros), cancellationToken);

            return Created("/api/envios", created);
        }
        catch (InvalidOperationException)
        {
            return Problem("El usuario de demo no está vinculado a un comercio.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    [HttpGet("envios")]
    public async Task<IActionResult> GetShipments(CancellationToken cancellationToken)
        => Ok(await listarEnvios.EjecutarAsync(cancellationToken));
}

public sealed record CrearEnvioRequest(
    [param: Required, StringLength(160)] string DestinatarioNombre,
    [param: Required, StringLength(240)] string Direccion,
    [param: Required, StringLength(80)] string CodigoBulto,
    [param: Range(typeof(decimal), "0.01", "999999999")] decimal PesoGramos,
    [param: Range(typeof(decimal), "0.01", "999999999")] decimal LargoCentimetros,
    [param: Range(typeof(decimal), "0.01", "999999999")] decimal AnchoCentimetros,
    [param: Range(typeof(decimal), "0.01", "999999999")] decimal AltoCentimetros);
