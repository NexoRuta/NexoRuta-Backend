using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NexoRuta.Api.Features.Envios.Contracts.Requests;
using NexoRuta.Application.Envios.Dtos;
using NexoRuta.Application.Envios.Exceptions;
using NexoRuta.Application.Envios.UseCases;
using NexoRuta.Application.Reparto.Exceptions;

namespace NexoRuta.Api.Features.Envios.Controllers;

[ApiController, Authorize(Policy = "envios.crear"), Authorize(Policy = "Comercio"), Route("api/envios/{id:guid}/destino")]
public sealed class DestinosEnvioController(GestionarDestinoEnvioUseCase casos) : ControllerBase
{
    [HttpGet, ProducesResponseType<DestinoEnvioDatos>(200), ProducesResponseType(403), ProducesResponseType(404)]
    public async Task<IActionResult> Obtener(Guid id, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var destino = await casos.ObtenerAsync(id, ct);
        return destino is null ? NotFound() : Ok(destino);
    }
    [HttpPost("propuestas"), EnableRateLimiting("geocodificacion")]
    [ProducesResponseType<PropuestasDestinoDatos>(200), ProducesResponseType(403), ProducesResponseType(404), ProducesResponseType(409), ProducesResponseType(429), ProducesResponseType(503)]
    public async Task<IActionResult> Proponer(Guid id, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        try { var propuestas = await casos.ProponerAsync(id, ct); return propuestas is null ? NotFound() : Ok(propuestas); }
        catch (ConflictoRepartoException ex) { return Problem(ex.Message, statusCode: 409); }
        catch (GeocodificacionNoDisponibleException ex) { return Problem(ex.Message, statusCode: 503); }
    }
    [HttpPut, ProducesResponseType(204), ProducesResponseType(400), ProducesResponseType(403), ProducesResponseType(404), ProducesResponseType(409)]
    public async Task<IActionResult> Confirmar(Guid id, ConfirmarDestinoRequest request, CancellationToken ct)
    {
        try { return await casos.ConfirmarAsync(id, request.Version!.Value, request.Latitud!.Value, request.Longitud!.Value, ct) ? NoContent() : NotFound(); }
        catch (ConflictoRepartoException ex) { return Problem(ex.Message, statusCode: 409); }
        catch (ArgumentException ex) { return Problem(ex.Message, statusCode: 400); }
    }
}
