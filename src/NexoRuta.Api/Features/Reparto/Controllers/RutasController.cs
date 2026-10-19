using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.Application.Reparto.Dtos;
using NexoRuta.Application.Reparto.Exceptions;
using NexoRuta.Application.Reparto.UseCases;
namespace NexoRuta.Api.Features.Reparto.Controllers;

[ApiController, Authorize, Route("api/rutas")]
public sealed class RutasController(GestionarRutasUseCase casos, ConfigurarRepartoUseCase configuracion,
    GestionarRepartidoresUseCase repartidores) : ControllerBase
{
    [HttpGet("recursos"), Authorize(Policy = "envios.gestionar")]
    [ProducesResponseType<RecursosPlanificacionResponse>(200)]
    [ProducesResponseType(403), ProducesResponseType(409)]
    public async Task<IActionResult> Recursos(CancellationToken ct)
    {
        try
        {
            var politica = await configuracion.ObtenerAsync(ct);
            if (politica is null) return Problem("Configurá las reglas de reparto antes de planificar.", statusCode: 409);
            return Ok(new RecursosPlanificacionResponse(ConfiguracionRepartoContract.Desde(politica),
                (await repartidores.ListarAsync(ct)).Where(r => r.Activo).Select(r => new RepartidorPlanificacionResponse(r.AccesoId, r.Nombre)).ToArray(),
                (await configuracion.VehiculosAsync(ct)).Where(v => v.Activo).Select(VehiculoContract.Desde).ToArray()));
        }
        catch (RepartoNoPermitidoException) { return Forbid(); }
    }
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
    [HttpGet("/api/mobile/rutas/{id:guid}"), Authorize(Policy = "reparto.ejecutar")]
    [ProducesResponseType<HojaRutaResponse>(200)]
    [ProducesResponseType(403), ProducesResponseType(404)]
    public async Task<IActionResult> Propia(Guid id, CancellationToken ct)
    {
        try { var hoja = await casos.ObtenerPropiaAsync(id, ct); return hoja is null ? NotFound() : Ok(HojaRutaResponse.Desde(hoja)); }
        catch (RepartoNoPermitidoException) { return Forbid(); }
    }
    [HttpPost("/api/envios/{id:guid}/reasignacion"), Authorize(Policy = "envios.gestionar")]
    [ProducesResponseType<HojaRutaResponse>(200)]
    [ProducesResponseType(400), ProducesResponseType(403), ProducesResponseType(404), ProducesResponseType(409)]
    public async Task<IActionResult> Reasignar(Guid id, ReasignarEnvioRequest request, CancellationToken ct)
    {
        try
        {
            var hoja = await casos.ReasignarAsync(id, new(request.RutaDestinoId, request.VersionEnvio,
                request.VersionRutaDestino, request.LlegadaEstimadaUtc, request.Motivo), ct);
            return hoja is null ? NotFound() : Ok(HojaRutaResponse.Desde(hoja));
        }
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
