using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.Application.Reparto.Dtos;
using NexoRuta.Application.Reparto.Exceptions;
using NexoRuta.Application.Reparto.UseCases;
namespace NexoRuta.Api.Features.Reparto.Controllers;

[ApiController, Authorize, Route("api/rutas")]
public sealed class RutasController(GestionarRutasUseCase casos) : ControllerBase
{
    [HttpGet, Authorize(Policy = "envios.gestionar")]
    [ProducesResponseType<HojaRutaResponse[]>(200)]
    public async Task<IActionResult> Listar([FromQuery] DateOnly fecha, CancellationToken ct)
    {
        try { return Ok((await casos.ListarAsync(fecha, false, ct)).Select(HojaRutaResponse.Desde).ToArray()); }
        catch (RepartoNoPermitidoException) { return Forbid(); }
    }
    [HttpGet("/api/mobile/rutas"), Authorize(Policy = "reparto.ejecutar")]
    [ProducesResponseType<HojaRutaResponse[]>(200)]
    public async Task<IActionResult> Propias([FromQuery] DateOnly fecha, CancellationToken ct)
    {
        try { return Ok((await casos.ListarAsync(fecha, true, ct)).Select(HojaRutaResponse.Desde).ToArray()); }
        catch (RepartoNoPermitidoException) { return Forbid(); }
    }
    [HttpPost, Authorize(Policy = "envios.gestionar")]
    [ProducesResponseType<HojaRutaResponse>(200)]
    [ProducesResponseType(400), ProducesResponseType(403), ProducesResponseType(409)]
    public async Task<IActionResult> Planificar(PlanificarRutaRequest request, CancellationToken ct)
    {
        try { return Ok(HojaRutaResponse.Desde(await casos.PlanificarAsync(new(request.RepartidorAccesoId,
            request.VehiculoId, request.Fecha, request.Paradas.Select(x => new ParadaPlanificadaDatos(x.EnvioId, x.LlegadaEstimadaUtc)).ToArray()), ct))); }
        catch (RepartoNoPermitidoException) { return Forbid(); }
        catch (ConflictoRepartoException ex) { return Problem(ex.Message, statusCode: 409); }
        catch (ArgumentException ex) { return Problem(ex.Message, statusCode: 400); }
    }
    [HttpPost("{id:guid}/despacho"), Authorize(Policy = "envios.gestionar")]
    [ProducesResponseType<HojaRutaResponse>(200)]
    [ProducesResponseType(404), ProducesResponseType(403), ProducesResponseType(409)]
    public async Task<IActionResult> Despachar(Guid id, VersionRutaRequest request, CancellationToken ct)
    {
        try { var hoja = await casos.DespacharAsync(id, request.Version, ct); return hoja is null ? NotFound() : Ok(HojaRutaResponse.Desde(hoja)); }
        catch (RepartoNoPermitidoException) { return Forbid(); }
        catch (ConflictoRepartoException ex) { return Problem(ex.Message, statusCode: 409); }
    }
    [HttpPost("/api/deposito/envios/{id:guid}/recepcion"), Authorize(Policy = "envios.gestionar")]
    [ProducesResponseType(204), ProducesResponseType(404), ProducesResponseType(403), ProducesResponseType(409)]
    public async Task<IActionResult> Recibir(Guid id, VersionRutaRequest request, CancellationToken ct)
    {
        try { return await casos.RecibirAsync(id, request.Version, ct) ? NoContent() : NotFound(); }
        catch (RepartoNoPermitidoException) { return Forbid(); }
        catch (ConflictoRepartoException ex) { return Problem(ex.Message, statusCode: 409); }
    }
    [HttpPut("/api/envios/{id:guid}/destino"), Authorize(Policy = "envios.crear")]
    [ProducesResponseType(204), ProducesResponseType(400), ProducesResponseType(404), ProducesResponseType(403), ProducesResponseType(409)]
    public async Task<IActionResult> Destino(Guid id, ConfirmarDestinoRequest request, CancellationToken ct)
    {
        try { return await casos.ConfirmarDestinoAsync(id, request.Version, request.Latitud, request.Longitud, ct) ? NoContent() : NotFound(); }
        catch (RepartoNoPermitidoException) { return Forbid(); }
        catch (ConflictoRepartoException ex) { return Problem(ex.Message, statusCode: 409); }
        catch (ArgumentException ex) { return Problem(ex.Message, statusCode: 400); }
    }
}
