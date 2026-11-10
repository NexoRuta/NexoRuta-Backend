using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Api.Features.Devoluciones.Contracts;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Envios.Devoluciones;
using NexoRuta.Application.Reparto.Exceptions;

namespace NexoRuta.Api.Features.Devoluciones.Controllers;

[ApiController, Authorize, Route("api/devoluciones")]
public sealed class DevolucionesController(GestionarDevolucionesUseCase devoluciones) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "envios.consultar")]
    [ProducesResponseType<PaginaDevolucionesResponse>(200)]
    [ProducesResponseType(400), ProducesResponseType(401), ProducesResponseType(403)]
    public async Task<IActionResult> Listar([FromQuery] Guid? operadorId, [FromQuery] DateTimeOffset desdeUtc,
        [FromQuery] DateTimeOffset hastaUtc, [FromQuery] string? situacion, [FromQuery] string? referencia,
        [FromQuery] string? cursor, [FromQuery] int limite = 50, CancellationToken ct = default)
    {
        SinCache();
        try
        {
            var pagina = await devoluciones.ListarAsync(operadorId, desdeUtc, hastaUtc, situacion, referencia, cursor, limite, ct);
            return Ok(new PaginaDevolucionesResponse(pagina.Items.Select(DevolucionResumenResponse.Desde).ToArray(), pagina.CursorSiguiente));
        }
        catch (AccesoNoPermitidoException) { return Forbid(); }
        catch (ArgumentException e) { return Problem(e.Message, statusCode: 400); }
    }

    [HttpGet("envios/{envioId:guid}")]
    [Authorize(Policy = "envios.consultar")]
    [ProducesResponseType<DevolucionDetalleResponse>(200)]
    [ProducesResponseType(400), ProducesResponseType(401), ProducesResponseType(403), ProducesResponseType(404)]
    public async Task<IActionResult> Obtener(Guid envioId, [FromQuery] Guid operadorId,
        [FromQuery] int desdeBulto = 0, [FromQuery] int limite = 100, CancellationToken ct = default)
    {
        SinCache();
        try
        {
            var detalle = await devoluciones.ObtenerAsync(operadorId, envioId, desdeBulto, limite, ct);
            return detalle is null ? NotFound() : Ok(DevolucionDetalleResponse.Desde(detalle));
        }
        catch (AccesoNoPermitidoException) { return Forbid(); }
        catch (ArgumentException e) { return Problem(e.Message, statusCode: 400); }
    }

    [HttpGet("envios/{envioId:guid}/historial")]
    [Authorize(Policy = "envios.consultar")]
    [ProducesResponseType<HechoDevolucionResponse[]>(200)]
    [ProducesResponseType(400), ProducesResponseType(401), ProducesResponseType(403), ProducesResponseType(404)]
    public async Task<IActionResult> Historial(Guid envioId, [FromQuery] Guid operadorId, CancellationToken ct)
    {
        SinCache();
        try
        {
            var hechos = await devoluciones.HistorialAsync(operadorId, envioId, ct);
            return hechos is null ? NotFound() : Ok(hechos.Select(HechoDevolucionResponse.Desde).ToArray());
        }
        catch (AccesoNoPermitidoException) { return Forbid(); }
        catch (ArgumentException e) { return Problem(e.Message, statusCode: 400); }
    }

    [HttpPost("envios/{envioId:guid}/transferencias")]
    [Authorize(Policy = "envios.gestionar")]
    [ProducesResponseType<TransferenciaDevolucionResponse>(200)]
    [ProducesResponseType(400), ProducesResponseType(401), ProducesResponseType(403), ProducesResponseType(404), ProducesResponseType(409)]
    public async Task<IActionResult> Transferir(Guid envioId, TransferirDevolucionRequest request, CancellationToken ct)
    {
        SinCache();
        try
        {
            var command = new TransferirDevolucionComando(request.ComandoId, request.VersionEnvio,
                request.Bultos.Select(x => new BultoDevolucionComando(x.BultoId, x.VersionBulto)).ToArray(),
                request.ConfirmarTransferenciaFisica);
            var result = await devoluciones.TransferirAsync(envioId, command, ct);
            return result is null ? NotFound() : Ok(new TransferenciaDevolucionResponse(
                result.TransferenciaId, result.Bultos, result.RegistradaEnUtc));
        }
        catch (AccesoNoPermitidoException) { return Forbid(); }
        catch (ArgumentException e) { return Problem(e.Message, statusCode: 400); }
        catch (ConflictoRepartoException e) { return Problem(e.Message, statusCode: 409); }
    }

    [HttpPost("transferencias/{transferenciaId:guid}/acuses")]
    [Authorize(Policy = "devoluciones.gestionar")]
    [ProducesResponseType<AcuseDevolucionResponse>(200)]
    [ProducesResponseType(400), ProducesResponseType(401), ProducesResponseType(403), ProducesResponseType(404), ProducesResponseType(409)]
    public async Task<IActionResult> Acusar(Guid transferenciaId, [FromQuery] Guid operadorId,
        AcusarDevolucionRequest request, CancellationToken ct)
    {
        SinCache();
        try
        {
            var command = new AcusarDevolucionComando(request.ComandoId, request.VersionTransferencia,
                request.Conforme, request.Bultos.Select(x => new LineaAcuseDevolucionComando(
                    x.BultoId, x.VersionBulto, x.Observacion)).ToArray());
            var result = await devoluciones.AcusarAsync(operadorId, transferenciaId, command, ct);
            return result is null ? NotFound() : Ok(new AcuseDevolucionResponse(
                result.AcuseId, result.Bultos, result.Resultado, result.RegistradoEnUtc));
        }
        catch (AccesoNoPermitidoException) { return Forbid(); }
        catch (ArgumentException e) { return Problem(e.Message, statusCode: 400); }
        catch (ConflictoRepartoException e) { return Problem(e.Message, statusCode: 409); }
    }

    private void SinCache() => Response.Headers.CacheControl = "no-store";
}
