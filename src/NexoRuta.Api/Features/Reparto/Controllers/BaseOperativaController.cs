using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.Application.Reparto.Exceptions;
using NexoRuta.Application.Reparto.UseCases;
using NexoRuta.Application.Envios.Exceptions;
namespace NexoRuta.Api.Features.Reparto.Controllers;
[ApiController, Route("api/reparto/base-operativa"), Authorize(Policy = "configuracion.gestionar")]
public sealed class BaseOperativaController(ConfigurarRepartoUseCase casos) : ControllerBase
{
    [HttpGet, ProducesResponseType<BaseOperativaResponse>(200), ProducesResponseType(403), ProducesResponseType(404)]
    public async Task<IActionResult> Obtener(CancellationToken ct)
    { try { var b = await casos.BaseAsync(ct); return b is null ? NotFound() : Ok(BaseOperativaResponse.Desde(b)); } catch(RepartoNoPermitidoException) { return Forbid(); } }
    [HttpPut, ProducesResponseType<BaseOperativaResponse>(200), ProducesResponseType(400), ProducesResponseType(403), ProducesResponseType(409)]
    public async Task<IActionResult> Guardar(GuardarBaseOperativaRequest r, CancellationToken ct)
    {
        try { return Ok(BaseOperativaResponse.Desde(await casos.GuardarBaseAsync(new(r.Version!.Value,r.Direccion,r.ServicioPredeterminadoMinutos,r.MargenRetornoMinutos),ct))); }
        catch(RepartoNoPermitidoException) { return Forbid(); } catch(ConflictoRepartoException e) { return Problem(e.Message,statusCode:409); } catch(ArgumentException e) { return Problem(e.Message,statusCode:400); }
    }
    [HttpPost("confirmacion"), ProducesResponseType<BaseOperativaResponse>(200), ProducesResponseType(400), ProducesResponseType(403), ProducesResponseType(404), ProducesResponseType(409)]
    public async Task<IActionResult> Confirmar(ConfirmarBaseOperativaRequest r, CancellationToken ct)
    {
        try { var b = await casos.ConfirmarBaseAsync(r.Version!.Value,r.Latitud!.Value,r.Longitud!.Value,ct); return b is null ? NotFound() : Ok(BaseOperativaResponse.Desde(b)); }
        catch(RepartoNoPermitidoException) { return Forbid(); } catch(ConflictoRepartoException e) { return Problem(e.Message,statusCode:409); } catch(ArgumentException e) { return Problem(e.Message,statusCode:400); }
    }
    [HttpPost("propuestas"), EnableRateLimiting("geocodificacion"), ProducesResponseType<PropuestasBaseResponse>(200), ProducesResponseType(403), ProducesResponseType(404), ProducesResponseType(429), ProducesResponseType(503)]
    public async Task<IActionResult> Proponer(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        try { var p = await casos.ProponerBaseAsync(ct); return p is null ? NotFound() : Ok(new PropuestasBaseResponse(p.Version,p.Direccion,p.Atribucion,p.Propuestas.Select(x=>new PropuestaBaseResponse(x.Descripcion,x.Latitud,x.Longitud)).ToArray())); }
        catch(RepartoNoPermitidoException) { return Forbid(); } catch(GeocodificacionNoDisponibleException e) { return Problem(e.Message,statusCode:503); }
        catch(GeocodificacionLimiteException e) { return Problem(e.Message,statusCode:429); }
    }
}
