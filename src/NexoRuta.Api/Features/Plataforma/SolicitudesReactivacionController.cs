using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Api.Features.Organizaciones.Contracts;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.UseCases;
using NexoRuta.Domain.Administracion;

namespace NexoRuta.Api.Features.Plataforma;

[ApiController]
[Route("api/admin/solicitudes-reactivacion")]
[Authorize(Policy = RolPlataforma.GestionarOrganizaciones)]
public sealed class SolicitudesReactivacionController(GestionarReactivacionesOrganizacionUseCase solicitudes) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<PaginaSolicitudesReactivacionResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Listar(string? estado, string? tipo, int pagina = 1, int tamanoPagina = 20,
        CancellationToken ct = default)
    {
        if (!ParseEstado(estado, out var state) || !ParseTipo(tipo, out var kind))
            return BadRequest(new { message = "Los filtros no son válidos." });
        try { return Ok(PaginaSolicitudesReactivacionResponse.Desde(
            await solicitudes.ListarAsync(state, kind, pagina, tamanoPagina, ct))); }
        catch (ArgumentException error) { return BadRequest(new { message = error.Message }); }
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<SolicitudReactivacionResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Obtener(Guid id, CancellationToken ct)
    {
        try { return Ok(SolicitudReactivacionResponse.Desde(await solicitudes.ObtenerAsync(id, ct))); }
        catch (SolicitudReactivacionNoEncontradaException error) { return NotFound(new { message = error.Message }); }
    }

    [HttpPost("{id:guid}/resolver")]
    [ProducesResponseType<SolicitudReactivacionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Resolver(Guid id, [FromBody] ResolverSolicitudReactivacionRequest request, CancellationToken ct)
    {
        if (HttpContext.Items[typeof(ContextoPlataforma)] is not ContextoPlataforma actor) return Forbid();
        var decision = request.Decision switch
        {
            "Aprobar" => EstadoSolicitudReactivacion.Aprobada,
            "Rechazar" => EstadoSolicitudReactivacion.Rechazada,
            _ => (EstadoSolicitudReactivacion?)null
        };
        if (decision is null) return BadRequest(new { message = "Elegí aprobar o rechazar." });
        try { return Ok(SolicitudReactivacionResponse.Desde(await solicitudes.ResolverAsync(
            id, actor.UsuarioId, decision.Value, request.Motivo, request.Version, ct))); }
        catch (SolicitudReactivacionNoEncontradaException error) { return NotFound(new { message = error.Message }); }
        catch (ConflictoEstadoOrganizacionException error) { return Conflict(new { message = error.Message }); }
        catch (ArgumentException error) { return BadRequest(new { message = error.Message }); }
    }

    private static bool ParseEstado(string? value, out EstadoSolicitudReactivacion? state)
    {
        state = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!Enum.TryParse<EstadoSolicitudReactivacion>(value, false, out var parsed) || !Enum.IsDefined(parsed)) return false;
        state = parsed;
        return true;
    }

    private static bool ParseTipo(string? value, out TipoAccesoUsuario? kind)
    {
        kind = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!Enum.TryParse<TipoAccesoUsuario>(value, false, out var parsed) ||
            parsed is not (TipoAccesoUsuario.Comercio or TipoAccesoUsuario.Operador)) return false;
        kind = parsed;
        return true;
    }
}
