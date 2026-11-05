using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Application.Reparto.Dtos;
using NexoRuta.Application.Reparto.Exceptions;
using NexoRuta.Application.Reparto.UseCases;
namespace NexoRuta.Api.Features.Reparto.Controllers;
[ApiController, Authorize(Policy="envios.gestionar"), Route("api/deposito")]
public sealed class RecepcionController(GestionarRutasUseCase casos) : ControllerBase
{
    [HttpPost("envios/{id:guid}/devolucion-comercio")]
    public async Task<IActionResult> DevolverComercio(Guid id, NexoRuta.Api.Features.Reparto.Contracts.VersionRutaRequest datos, CancellationToken ct)
    {
        try { return await casos.DevolverBultosComercioAsync(id, datos.Version, ct) ? NoContent() : NotFound(); }
        catch (RepartoNoPermitidoException) { return Forbid(); }
        catch (ConflictoRepartoException e) { return Problem(e.Message, statusCode: 409); }
    }
    [HttpPost("retiros/{id:guid}/entrega-comercio")]
    public async Task<IActionResult> EntregarComercio(Guid id, NexoRuta.Api.Features.Reparto.Contracts.VersionRutaRequest datos, CancellationToken ct)
    {
        try { return await casos.EntregarComercioAsync(id, datos.Version, ct) ? NoContent() : NotFound(); }
        catch (RepartoNoPermitidoException) { return Forbid(); }
        catch (ConflictoRepartoException e) { return Problem(e.Message, statusCode: 409); }
    }
    [HttpGet("consulta")]
    [ProducesResponseType<ConsultaRecepcionDatos>(200)]
    [ProducesResponseType(400), ProducesResponseType(401), ProducesResponseType(403), ProducesResponseType(404), ProducesResponseType(409)]
    public async Task<IActionResult> Consultar([FromQuery] string codigo, CancellationToken ct)
    {
        try { var r=await casos.ConsultarRecepcionAsync(codigo,ct); return r is null ? NotFound() : Ok(r); }
        catch(RepartoNoPermitidoException) { return Forbid(); }
        catch(ArgumentException e) { return Problem(e.Message,statusCode:400); }
        catch(ConflictoRepartoException e) { return Problem(e.Message,statusCode:409); }
    }
    [HttpPost("bultos/{id:guid}/recepcion")]
    [ProducesResponseType<ResultadoRecepcionDatos>(200)]
    [ProducesResponseType(400), ProducesResponseType(401), ProducesResponseType(403), ProducesResponseType(404), ProducesResponseType(409)]
    public async Task<IActionResult> Cotejar(Guid id, CotejoBultoDatos datos, CancellationToken ct)
    {
        try { var r=await casos.CotejarBultoAsync(id,datos,ct); return r is null ? NotFound() : Ok(r); }
        catch(RepartoNoPermitidoException) { return Forbid(); }
        catch(ArgumentException e) { return Problem(e.Message,statusCode:400); }
        catch(ConflictoRepartoException e) { return Problem(e.Message,statusCode:409); }
    }
}
