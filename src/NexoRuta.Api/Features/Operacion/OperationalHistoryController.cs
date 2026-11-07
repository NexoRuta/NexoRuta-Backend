using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Api.Features.Operacion.Contracts.Requests;
using NexoRuta.Api.Features.Operacion.Contracts.Responses;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Operacion.Exceptions;
using NexoRuta.Application.Operacion.UseCases;

namespace NexoRuta.Api.Features.Operacion;

[ApiController, Route("api/operacion"), Authorize(Policy = "envios.consultar")]
public sealed class OperationalHistoryController(
    ConsultarHistorialOperativoUseCase historial,
    ConsultarVolumenOperativoUseCase volumen) : ControllerBase
{
    [HttpGet("historial")]
    [ProducesResponseType<HistorialOperativoResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> History([FromQuery] ConsultarHistorialOperativoRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            var result = await historial.EjecutarAsync(request.OperadorId!.Value, request.Desde!.Value,
                request.Hasta!.Value, request.EnvioId, request.Tipo, request.Offset, request.Limite, ct);
            return Ok(HistorialOperativoResponse.Desde(result));
        }
        catch (AccesoActualNoDisponibleException)
        {
            return Problem("El ámbito de acceso no está disponible.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (AccesoNoPermitidoException) { return Forbid(); }
        catch (ArgumentException error) { return Problem(error.Message, statusCode: StatusCodes.Status400BadRequest); }
        catch (HistorialOperativoNoDisponibleException)
        {
            return Problem("El historial documental está temporalmente indisponible. Reintentá más tarde.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    [HttpGet("analitica/volumen")]
    [ProducesResponseType<VolumenOperativoResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Volumes([FromQuery] ConsultarVolumenOperativoRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            var result = await volumen.EjecutarAsync(request.OperadorId!.Value,
                request.Desde!.Value, request.Hasta!.Value, ct);
            return Ok(VolumenOperativoResponse.FromResult(result));
        }
        catch (AccesoActualNoDisponibleException)
        {
            return Problem("El ámbito de acceso no está disponible.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (AccesoNoPermitidoException) { return Forbid(); }
        catch (ArgumentException error) { return Problem(error.Message, statusCode: StatusCodes.Status400BadRequest); }
        catch (HistorialOperativoNoDisponibleException)
        {
            return Problem("La analítica documental está temporalmente indisponible. Reintentá más tarde.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
