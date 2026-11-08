using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Gestion;
using Npgsql;
using NexoRuta.Api.Features.Gestion.Contracts;
namespace NexoRuta.Api.Features.Gestion;

[ApiController, Authorize(Policy="envios.consultar"), Route("api/gestion/operadores/{operador:guid}")]
public sealed class GestionController(GestionUseCases casos) : ControllerBase
{
    [HttpGet("opciones/{tipo}"), ProducesResponseType<OpcionesGestionResponse>(200)]
    public Task<IActionResult> Opciones(Guid operador, string tipo, [FromQuery] string? buscar, [FromQuery] int pagina=1, [FromQuery] Guid[]? seleccionado=null, CancellationToken ct=default)
        => Ejecutar(async()=>OpcionesGestionResponse.Desde(await casos.OpcionesAsync(operador,tipo,buscar,pagina,seleccionado??[],ct)));
    [HttpGet("reportes"), ProducesResponseType<ReporteGestionResponse>(200)]
    public Task<IActionResult> Reporte(Guid operador, [FromQuery] DateTimeOffset desde, [FromQuery] DateTimeOffset hasta,
        [FromQuery] Guid? comercioId, [FromQuery] Guid? zonaId, [FromQuery] Guid? repartidorId, [FromQuery] int pagina=1, [FromQuery] int tamano=25, CancellationToken ct=default)
        => Ejecutar(async()=>ReporteGestionResponse.Desde(await casos.ReporteAsync(new(operador,desde,hasta,comercioId,zonaId,repartidorId,pagina,tamano),ct)));
    [HttpGet("reportes/operaciones/{envio:guid}"), ProducesResponseType<DetalleGestionResponse>(200), ProducesResponseType(404)]
    public Task<IActionResult> Detalle(Guid operador, Guid envio, CancellationToken ct)
        => Ejecutar(async()=>await casos.DetalleAsync(operador,envio,ct) is {} d ? DetalleGestionResponse.Desde(d) : null);
    [HttpGet("cuenta"), ProducesResponseType<CuentaGestionResponse>(200)]
    public Task<IActionResult> Cuenta(Guid operador, [FromQuery] DateTimeOffset desde, [FromQuery] DateTimeOffset hasta,
        [FromQuery] Guid? comercioId, [FromQuery] int pagina=1, [FromQuery] int tamano=25, CancellationToken ct=default)
        => Ejecutar(async()=>CuentaGestionResponse.Desde(await casos.CuentaAsync(new(operador,desde,hasta,comercioId,Pagina:pagina,Tamano:tamano),ct)));
    [HttpGet("liquidaciones/{id:guid}"), ProducesResponseType<LiquidacionDetalleResponse>(200), ProducesResponseType(404)]
    public Task<IActionResult> Documento(Guid operador, Guid id, CancellationToken ct)
        => Ejecutar(async()=>await casos.LiquidacionAsync(operador,id,ct) is {} d ? LiquidacionDetalleResponse.Desde(d) : null);
    [HttpPost("liquidaciones"), Authorize(Policy="envios.gestionar"), ProducesResponseType<LiquidacionDetalleResponse>(200)]
    public Task<IActionResult> Preparar(Guid operador, PrepararLiquidacionRequest command, CancellationToken ct)
        => Ejecutar(async()=>LiquidacionDetalleResponse.Desde(await casos.PrepararAsync(operador,command.ToApplication(),ct)));
    [HttpPost("liquidaciones/{id:guid}/confirmar"), Authorize(Policy="envios.gestionar"), ProducesResponseType<LiquidacionDetalleResponse>(200)]
    public Task<IActionResult> Confirmar(Guid operador, Guid id, CancellationToken ct)
        => Ejecutar(async()=>await casos.TransicionAsync(operador,id,true,ct) is {} d ? LiquidacionDetalleResponse.Desde(d) : null);
    [HttpPost("liquidaciones/{id:guid}/descartar"), Authorize(Policy="envios.gestionar"), ProducesResponseType<LiquidacionDetalleResponse>(200)]
    public Task<IActionResult> Descartar(Guid operador, Guid id, CancellationToken ct)
        => Ejecutar(async()=>await casos.TransicionAsync(operador,id,false,ct) is {} d ? LiquidacionDetalleResponse.Desde(d) : null);
    [HttpPost("correcciones"), Authorize(Policy="envios.gestionar"), ProducesResponseType<CorreccionRegistradaResponse>(200)]
    public Task<IActionResult> Corregir(Guid operador, CorregirCuentaRequest command, CancellationToken ct)
        => Ejecutar(async()=>new CorreccionRegistradaResponse(await casos.CorregirAsync(operador,command.ToApplication(),ct)));
    private async Task<IActionResult> Ejecutar(Func<Task<object?>> action)
    {
        Response.Headers.CacheControl="no-store";
        try { var result=await action(); return result is null ? NotFound() : Ok(result); }
        catch(AccesoNoPermitidoException) { return Forbid(); }
        catch(NexoRuta.Domain.Gestion.ConflictoLiquidacionException e) { return Problem(e.Message,statusCode:409); }
        catch(ArgumentException e) { return Problem(e.Message,statusCode:400); }
        catch(PostgresException e) when(e.SqlState is "NXR02" or "23505") { return Problem("El documento cambió o incluye conceptos ya liquidados. Recargá y revisá el detalle.",statusCode:409); }
        catch(PostgresException e) when(e.SqlState=="22023") { return Problem(e.MessageText,statusCode:400); }
        catch(PostgresException e) when(e.SqlState=="42501") { return Forbid(); }
    }
}
