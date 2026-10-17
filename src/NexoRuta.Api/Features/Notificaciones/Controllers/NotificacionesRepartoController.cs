using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Api.Core.Security;
using NexoRuta.Api.Features.Notificaciones.Contracts;
using NexoRuta.Application.Notificaciones.UseCases;
using NexoRuta.Application.Reparto.Exceptions;
namespace NexoRuta.Api.Features.Notificaciones.Controllers;
[ApiController, Authorize(Policy = "reparto.ejecutar"), Route("api/mobile")]
public sealed class NotificacionesRepartoController(NotificacionesRepartoUseCase casos) : ControllerBase
{
    [HttpPut("instalaciones")]
    [ProducesResponseType(204), ProducesResponseType(400), ProducesResponseType(403), ProducesResponseType(409), ProducesResponseType(503)]
    public async Task<IActionResult> Registrar(RegistroInstalacionRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst(JwtSesionClaims.SesionMovil)?.Value, out var sesionId)) return Forbid();
        try { await casos.RegistrarAsync(sesionId, request.InstalacionId, request.Plataforma, request.FirebaseInstallationId, request.Permitida, ct); return NoContent(); }
        catch (RepartoNoPermitidoException) { return Forbid(); }
        catch (ConflictoRepartoException ex) { return Problem(ex.Message, statusCode: 409); }
        catch (ArgumentException ex) { return Problem(ex.Message, statusCode: 400); }
        catch (InvalidOperationException) { return Problem("El registro de notificaciones no está disponible.", statusCode: 503); }
    }
    [HttpGet("notificaciones")]
    [ProducesResponseType<AvisoRepartoResponse[]>(200), ProducesResponseType(403)]
    public async Task<IActionResult> Listar(CancellationToken ct)
    {
        try { return Ok((await casos.ListarAsync(ct)).Select(AvisoRepartoResponse.Desde).ToArray()); }
        catch (RepartoNoPermitidoException) { return Forbid(); }
    }
    [HttpGet("notificaciones/{id:guid}")]
    [ProducesResponseType<AvisoRepartoResponse>(200), ProducesResponseType(403), ProducesResponseType(404)]
    public async Task<IActionResult> Obtener(Guid id, CancellationToken ct)
    {
        try { var aviso = await casos.ObtenerAsync(id, null, ct); return aviso is null ? NotFound() : Ok(AvisoRepartoResponse.Desde(aviso)); }
        catch (RepartoNoPermitidoException) { return Forbid(); }
    }
    [HttpPost("notificaciones/{id:guid}/confirmaciones")]
    [ProducesResponseType<AvisoRepartoResponse>(200), ProducesResponseType(400), ProducesResponseType(403), ProducesResponseType(404)]
    public async Task<IActionResult> Confirmar(Guid id, RegistroAvisoRequest request, CancellationToken ct)
    {
        try { var aviso = await casos.ObtenerAsync(id, request.Tipo, ct); return aviso is null ? NotFound() : Ok(AvisoRepartoResponse.Desde(aviso)); }
        catch (RepartoNoPermitidoException) { return Forbid(); }
        catch (ArgumentException ex) { return Problem(ex.Message, statusCode: 400); }
    }
}
