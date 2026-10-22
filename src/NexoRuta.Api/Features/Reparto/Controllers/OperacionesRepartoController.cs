using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.Application.Reparto.Exceptions;
using NexoRuta.Application.Reparto.UseCases;
namespace NexoRuta.Api.Features.Reparto.Controllers;
[ApiController, Authorize(Policy = "reparto.ejecutar"), Route("api/mobile/jornadas/{jornadaId:guid}")]
public sealed class OperacionesRepartoController(EjecutarRepartoUseCase casos) : ControllerBase
{
    [HttpPost("operaciones"), RequestSizeLimit(32768)]
    [ProducesResponseType<ResultadoOperacionResponse>(200)]
    [ProducesResponseType(400), ProducesResponseType(403), ProducesResponseType(404), ProducesResponseType(409)]
    public async Task<IActionResult> Aplicar(Guid jornadaId, OperacionRepartoRequest request, CancellationToken ct)
    {
        try
        {
            var resultado = await casos.AplicarAsync(jornadaId, new(request.Id, request.Secuencia, request.Tipo,
                request.EnvioId, request.VersionEnvio, request.OcurridaEn, request.Datos), ct);
            return resultado is null ? NotFound() : Ok(ResultadoOperacionResponse.Desde(resultado));
        }
        catch (RepartoNoPermitidoException) { return Forbid(); }
        catch (ConflictoRepartoException ex) { return Problem(ex.Message, statusCode: 409); }
        catch (ArgumentException ex) { return Problem(ex.Message, statusCode: 400); }
    }
    [HttpPut("evidencias/{archivoId:guid}"), RequestSizeLimit(3000000)]
    [ProducesResponseType(204), ProducesResponseType(400), ProducesResponseType(403), ProducesResponseType(404), ProducesResponseType(409), ProducesResponseType(503)]
    public async Task<IActionResult> Evidencia(Guid jornadaId, Guid archivoId, EvidenciaRepartoRequest request, CancellationToken ct)
    {
        try { return await casos.EvidenciaAsync(jornadaId, request.EnvioId, archivoId, request.Tipo, request.Contenido, ct) ? NoContent() : NotFound(); }
        catch (RepartoNoPermitidoException) { return Forbid(); }
        catch (ConflictoRepartoException ex) { return Problem(ex.Message, statusCode: 409); }
        catch (ArgumentException ex) { return Problem(ex.Message, statusCode: 400); }
        catch (InvalidOperationException) { return Problem("El almacenamiento de evidencias no está disponible.", statusCode: 503); }
    }
}
