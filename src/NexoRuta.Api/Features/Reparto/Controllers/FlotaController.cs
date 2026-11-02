using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.Application.Reparto.Exceptions;
using NexoRuta.Application.Reparto.UseCases;

namespace NexoRuta.Api.Features.Reparto.Controllers;

[ApiController, Route("api/reparto/flota"), Authorize(Policy = "envios.gestionar"), Authorize(Policy = "envios.consultar")]
public sealed class FlotaController(GestionarRutasUseCase casos) : ControllerBase
{
    [HttpGet, ProducesResponseType<VehiculoFlotaResponse[]>(200), ProducesResponseType(403)]
    public async Task<IActionResult> Listar(CancellationToken ct)
    {
        try { return Ok((await casos.FlotaAsync(ct)).Select(VehiculoFlotaResponse.Desde).ToArray()); }
        catch (RepartoNoPermitidoException) { return Forbid(); }
    }
    [HttpPost("{vehiculoId:guid}/{accion}"), ProducesResponseType<ResultadoFlotaResponse>(200)]
    [ProducesResponseType(400), ProducesResponseType(403), ProducesResponseType(404), ProducesResponseType(409)]
    public async Task<IActionResult> Confirmar(Guid vehiculoId, string accion, ComandoFlotaRequest request, CancellationToken ct)
    {
        try
        {
            var resultado = await casos.ConfirmarFlotaAsync(vehiculoId, accion, request.Datos(), ct);
            return resultado is null ? NotFound() : Ok(ResultadoFlotaResponse.Desde(resultado));
        }
        catch (RepartoNoPermitidoException) { return Forbid(); }
        catch (ConflictoRepartoException ex) { return Problem(ex.Message, statusCode: 409); }
        catch (ArgumentException ex) { return Problem(ex.Message, statusCode: 400); }
    }
}
