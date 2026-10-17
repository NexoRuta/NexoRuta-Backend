using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Api.Features.Organizaciones.Contracts;
using NexoRuta.Api.Features.Plataforma.Contracts;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.UseCases;
using NexoRuta.Domain.Administracion;

namespace NexoRuta.Api.Features.Plataforma;

[ApiController]
[Route("api/admin/solicitudes-baja")]
[Authorize(Policy = RolPlataforma.GestionarOrganizaciones)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class SolicitudesBajaController(GestionarBajasOrganizacionUseCase solicitudes) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<PaginaSolicitudesBajaResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Listar(
        [FromQuery] string? estado,
        [FromQuery] string? tipo,
        [FromQuery] int pagina = 1,
        [FromQuery] int tamanoPagina = 20,
        CancellationToken ct = default)
    {
        if (!TryParseEstado(estado, out var parsedState) || !TryParseTipo(tipo, out var parsedType))
            return BadRequest(new { message = "Los filtros estado o tipo no son válidos." });
        try
        {
            var page = await solicitudes.ListarAdministrativasAsync(parsedState, parsedType, pagina, tamanoPagina, ct);
            return Ok(PaginaSolicitudesBajaResponse.Desde(page));
        }
        catch (ArgumentException error) { return BadRequest(new { message = error.Message }); }
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<SolicitudBajaResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Obtener(Guid id, CancellationToken ct)
    {
        try { return Ok(SolicitudBajaResponse.Desde(await solicitudes.ObtenerAdministrativaAsync(id, ct))); }
        catch (SolicitudBajaNoEncontradaException error) { return NotFound(new { message = error.Message }); }
    }

    [HttpPost("{id:guid}/resolver")]
    [ProducesResponseType<SolicitudBajaResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Resolver(Guid id, [FromBody] ResolverSolicitudBajaRequest request, CancellationToken ct)
    {
        if (HttpContext.Items[typeof(ContextoPlataforma)] is not ContextoPlataforma actor)
            return Forbid();
        var decision = request.Decision switch
        {
            "Aprobar" => EstadoSolicitudBaja.Aprobada,
            "Rechazar" => EstadoSolicitudBaja.Rechazada,
            _ => (EstadoSolicitudBaja?)null
        };
        if (decision is null) return BadRequest(new { message = "La decisión debe ser Aprobar o Rechazar." });
        try
        {
            var resolved = await solicitudes.ResolverAsync(id, actor.UsuarioId, decision.Value,
                request.Motivo, request.Version, ct);
            return Ok(SolicitudBajaResponse.Desde(resolved));
        }
        catch (SolicitudBajaNoEncontradaException error) { return NotFound(new { message = error.Message }); }
        catch (ConflictoEstadoOrganizacionException error) { return Conflict(new { message = error.Message }); }
        catch (ArgumentException error) { return BadRequest(new { message = error.Message }); }
    }

    private static bool TryParseEstado(string? value, out EstadoSolicitudBaja? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!Enum.TryParse(value, ignoreCase: false, out EstadoSolicitudBaja result) ||
            !Enum.IsDefined(result)) return false;
        parsed = result;
        return true;
    }

    private static bool TryParseTipo(string? value, out TipoAccesoUsuario? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!Enum.TryParse(value, ignoreCase: false, out TipoAccesoUsuario result) ||
            result is not (TipoAccesoUsuario.Operador or TipoAccesoUsuario.Comercio)) return false;
        parsed = result;
        return true;
    }
}
