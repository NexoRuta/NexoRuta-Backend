using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.Application.Reparto.Exceptions;
using NexoRuta.Application.Reparto.UseCases;

namespace NexoRuta.Api.Features.Reparto.Controllers;

[ApiController]
[Authorize(Policy = "configuracion.gestionar")]
[Route("api/reparto")]
public sealed class ConfiguracionRepartoController(ConfigurarRepartoUseCase casos) : ControllerBase
{
    [HttpGet("configuracion")]
    [ProducesResponseType<ConfiguracionRepartoContract>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Obtener(CancellationToken ct)
    {
        try
        {
            var actual = await casos.ObtenerAsync(ct);
            return actual is null ? NotFound() : Ok(ConfiguracionRepartoContract.Desde(actual));
        }
        catch (RepartoNoPermitidoException) { return Forbid(); }
    }

    [HttpPut("configuracion")]
    [ProducesResponseType<ConfiguracionRepartoContract>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Guardar(ConfiguracionRepartoContract request, CancellationToken ct)
    {
        try { return Ok(ConfiguracionRepartoContract.Desde(await casos.GuardarAsync(request.Datos(), ct))); }
        catch (RepartoNoPermitidoException) { return Forbid(); }
        catch (ConflictoRepartoException ex) { return Problem(ex.Message, statusCode: 409); }
        catch (ArgumentException ex) { return Problem(ex.Message, statusCode: 400); }
    }

    [HttpGet("vehiculos")]
    [ProducesResponseType<VehiculoContract[]>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Vehiculos(CancellationToken ct)
    {
        try { return Ok((await casos.VehiculosAsync(ct)).Select(VehiculoContract.Desde).ToArray()); }
        catch (RepartoNoPermitidoException) { return Forbid(); }
    }

    [HttpPut("vehiculos")]
    [ProducesResponseType<VehiculoContract>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Vehiculo(VehiculoContract request, CancellationToken ct)
    {
        try
        {
            var datos = await casos.GuardarVehiculoAsync(request.Datos(), ct);
            return datos is null ? NotFound() : Ok(VehiculoContract.Desde(datos));
        }
        catch (RepartoNoPermitidoException) { return Forbid(); }
        catch (ConflictoRepartoException ex) { return Problem(ex.Message, statusCode: 409); }
        catch (ArgumentException ex) { return Problem(ex.Message, statusCode: 400); }
    }
}
