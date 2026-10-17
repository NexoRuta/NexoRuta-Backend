using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Api.Features.Plataforma.Contracts;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.UseCases;
using NexoRuta.Domain.Administracion;

namespace NexoRuta.Api.Features.Plataforma;

[ApiController]
[Route("api/admin")]
[Authorize(Policy = RolPlataforma.GestionarOrganizaciones)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class CambiarEstadoOrganizacionController(GestionarBajasOrganizacionUseCase organizaciones) : ControllerBase
{
    [HttpPut("{tipo}/{id:guid}/estado")]
    [ProducesResponseType<EstadoOrganizacionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cambiar(string tipo, Guid id,
        [FromBody] CambiarEstadoOrganizacionRequest request, CancellationToken ct)
    {
        if (HttpContext.Items[typeof(ContextoPlataforma)] is not ContextoPlataforma actor)
            return Forbid();
        var organizationType = tipo switch
        {
            "operadores" => TipoAccesoUsuario.Operador,
            "comercios" => TipoAccesoUsuario.Comercio,
            _ => (TipoAccesoUsuario?)null
        };
        var state = request.Estado switch
        {
            "Activa" => EstadoOrganizacion.Activa,
            "Suspendida" => EstadoOrganizacion.Suspendida,
            _ => (EstadoOrganizacion?)null
        };
        if (organizationType is null || state is null)
            return BadRequest(new { message = "Tipo o estado de organización no válido." });
        try
        {
            var result = await organizaciones.CambiarEstadoAsync(organizationType.Value, id,
                actor.UsuarioId, state.Value, request.Motivo, request.Version, ct);
            return Ok(EstadoOrganizacionResponse.Desde(result));
        }
        catch (OrganizacionActualNoEncontradaException error) { return NotFound(new { message = error.Message }); }
        catch (ConflictoEstadoOrganizacionException error) { return Conflict(new { message = error.Message }); }
        catch (ArgumentException error) { return BadRequest(new { message = error.Message }); }
    }
}
