using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Api.Features.Organizaciones.Contracts;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.UseCases;
using NexoRuta.Application.Administracion.Repositories;

namespace NexoRuta.Api.Features.Organizaciones.Controllers;

[ApiController]
[Route("api/organizacion/suspendida")]
[Authorize(Policy = "organizacion-suspendida")]
public sealed class OrganizacionSuspendidaController(
    GestionarReactivacionesOrganizacionUseCase solicitudes, IAccesosUsuarioRepository accesos) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<OrganizacionSuspendidaResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Obtener(CancellationToken ct)
    {
        if (HttpContext.Items[typeof(ContextoOrganizacionSuspendida)] is not ContextoOrganizacionSuspendida context)
            return Forbid();
        var owner = await accesos.EsPropietarioAsync(context.UsuarioId, context.AccesoId, ct);
        return Ok(new OrganizacionSuspendidaResponse(context.OrganizacionId, context.TipoOrganizacion.ToString(),
            context.NombreOrganizacion, "Suspendida",
            "La organización está suspendida. Las operaciones volverán a estar disponibles después de su reactivación; luego deberás ingresar nuevamente.", owner));
    }

    [HttpGet("solicitudes-reactivacion")]
    [ProducesResponseType<IReadOnlyList<SolicitudReactivacionResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListarSolicitudes(CancellationToken ct)
    {
        if (HttpContext.Items[typeof(ContextoOrganizacionSuspendida)] is not ContextoOrganizacionSuspendida actor)
            return Forbid();
        if (!await accesos.EsPropietarioAsync(actor.UsuarioId, actor.AccesoId, ct)) return Forbid();
        var result = await solicitudes.ListarPropiasAsync(actor, ct);
        return Ok(result.Select(SolicitudReactivacionResponse.Desde).ToArray());
    }

    [HttpPost("solicitudes-reactivacion")]
    [ProducesResponseType<SolicitudReactivacionResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Solicitar([FromBody] SolicitarReactivacionRequest request, CancellationToken ct)
    {
        if (HttpContext.Items[typeof(ContextoOrganizacionSuspendida)] is not ContextoOrganizacionSuspendida actor)
            return Forbid();
        try
        {
            var result = await solicitudes.SolicitarAsync(actor, request.Motivo, ct);
            return Created("/api/organizacion/suspendida/solicitudes-reactivacion", SolicitudReactivacionResponse.Desde(result));
        }
        catch (AccesoNoPermitidoException) { return Forbid(); }
        catch (ConflictoEstadoOrganizacionException error) { return Conflict(new { message = error.Message }); }
        catch (ArgumentException error) { return BadRequest(new { message = error.Message }); }
    }
}
