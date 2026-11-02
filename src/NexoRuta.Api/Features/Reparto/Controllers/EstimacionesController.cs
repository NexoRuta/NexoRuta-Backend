using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.Application.Reparto.UseCases;
using NexoRuta.Application.Reparto.Exceptions;
namespace NexoRuta.Api.Features.Reparto.Controllers;
[ApiController,Authorize]
public sealed class EstimacionesController(GestionarRutasUseCase casos):ControllerBase
{
    [HttpGet("api/reparto/perfiles-viales"),Authorize(Policy="configuracion.gestionar"),ProducesResponseType<PerfilVialResponse[]>(200),ProducesResponseType(403)]
    public async Task<IActionResult> Perfiles(CancellationToken ct)
    {try{return Ok((await casos.PerfilesVialesAsync(ct)).Select(PerfilVialResponse.Desde).ToArray());}catch(RepartoNoPermitidoException){return Forbid();}}
    [HttpPut("api/reparto/perfiles-viales/{id:guid}"),Authorize(Policy="configuracion.gestionar"),ProducesResponseType<PerfilVialResponse>(200),ProducesResponseType(400),ProducesResponseType(403),ProducesResponseType(404),ProducesResponseType(409)]
    public async Task<IActionResult> Perfil(Guid id,ConfigurarPerfilVialRequest r,CancellationToken ct)
    {try{var p=await casos.ConfigurarPerfilAsync(id,new(r.Version!.Value,r.Compatibilidad,r.ConfirmarCompatibilidad,r.Motivo),ct);return p is null?NotFound():Ok(PerfilVialResponse.Desde(p));}catch(RepartoNoPermitidoException){return Forbid();}catch(ConflictoRepartoException e){return Problem(e.Message,statusCode:409);}catch(ArgumentException e){return Problem(e.Message,statusCode:400);}}
    [HttpPost("api/rutas/{id:guid}/estimaciones"),Authorize(Policy="envios.gestionar"),ProducesResponseType<EstimacionRutaResponse>(200),ProducesResponseType(400),ProducesResponseType(403),ProducesResponseType(404),ProducesResponseType(409)]
    public async Task<IActionResult> Calcular(Guid id,CalcularEstimacionRequest r,CancellationToken ct)
    {try{var p=await casos.EstimarAsync(id,new(r.ComandoId,r.VersionRuta!.Value),ct);return p is null?NotFound():Ok(EstimacionRutaResponse.Desde(p));}catch(RepartoNoPermitidoException){return Forbid();}catch(ConflictoRepartoException e){return Problem(e.Message,statusCode:409);}catch(ArgumentException e){return Problem(e.Message,statusCode:400);}}
    [HttpPost("api/rutas/{id:guid}/estimaciones/recorrido"),Authorize(Policy="envios.gestionar"),ProducesResponseType<EstimacionRutaResponse>(200),ProducesResponseType(400),ProducesResponseType(403),ProducesResponseType(404),ProducesResponseType(409)]
    public async Task<IActionResult> Actualizar(Guid id,ActualizarEstimacionRequest r,CancellationToken ct)
    {if(r.OcupacionId==Guid.Empty)return Problem("Indicá la ocupación exacta de la ruta.",statusCode:400);
        try{var p=await casos.EstimarAsync(id,new(r.ComandoId,r.VersionRuta!.Value,r.OcupacionId),ct);return p is null?NotFound():Ok(EstimacionRutaResponse.Desde(p));}catch(RepartoNoPermitidoException){return Forbid();}catch(ConflictoRepartoException e){return Problem(e.Message,statusCode:409);}catch(ArgumentException e){return Problem(e.Message,statusCode:400);}}
    [HttpGet("api/rutas/{id:guid}/estimaciones"),Authorize(Policy="envios.gestionar"),ProducesResponseType<EstimacionRutaResponse[]>(200),ProducesResponseType(403),ProducesResponseType(404)]
    public async Task<IActionResult> Historial(Guid id,[FromQuery]long? antesDeVersion,CancellationToken ct)
    {try{var p=await casos.EstimacionesAsync(id,antesDeVersion,ct);return p is null?NotFound():Ok(p.Select(EstimacionRutaResponse.Desde).ToArray());}catch(RepartoNoPermitidoException){return Forbid();}}
}
