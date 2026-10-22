using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Api.Features.Envios.Contracts.Requests;
using NexoRuta.Application.Envios.Results;
using NexoRuta.Application.Envios.UseCases;
using NexoRuta.Application.Reparto.Exceptions;
using NexoRuta.Application.Administracion.Exceptions;

namespace NexoRuta.Api.Features.Envios.Controllers;

[ApiController, Authorize, Route("api/envios/{envioId:guid}/cancelacion")]
public sealed class CancelacionesEnvioController(CancelarEnvioUseCase cancelar) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<EnvioCancelado>(200)]
    [ProducesResponseType(400), ProducesResponseType(401), ProducesResponseType(403), ProducesResponseType(404), ProducesResponseType(409)]
    public async Task<IActionResult> Cancelar(Guid envioId, CancelarEnvioRequest request, CancellationToken ct)
    {
        try { var resultado = await cancelar.EjecutarAsync(envioId, request.Version, request.Motivo, ct); return resultado is null ? NotFound() : Ok(resultado); }
        catch (RepartoNoPermitidoException) { return Forbid(); }
        catch (AccesoActualNoDisponibleException) { return Forbid(); }
        catch (ConflictoRepartoException ex) { return Problem(ex.Message, statusCode: 409); }
        catch (ArgumentException ex) { return Problem(ex.Message, statusCode: 400); }
    }
}
