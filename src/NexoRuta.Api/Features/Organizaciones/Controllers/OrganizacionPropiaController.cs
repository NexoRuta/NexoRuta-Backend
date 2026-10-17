using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Api.Features.Organizaciones.Contracts;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.UseCases;

namespace NexoRuta.Api.Features.Organizaciones.Controllers;

[ApiController]
[Authorize(Policy = "organizacion")]
[Route("api/organizacion/actual")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class OrganizacionPropiaController(
    ConsultarOrganizacionPropiaUseCase consultar,
    EditarNombreOrganizacionPropiaUseCase editar,
    GestionarBajasOrganizacionUseCase bajas) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<OrganizacionPropiaResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OrganizacionPropiaResponse>> Obtener(CancellationToken ct)
    {
        try
        {
            var organization = await consultar.EjecutarAsync(ct);
            return Ok(Desde(organization));
        }
        catch (AccesoNoPermitidoException) { return Forbid(); }
        catch (OrganizacionActualNoEncontradaException exception)
        { return NotFound(new { message = exception.Message }); }
    }

    [HttpPut]
    [ProducesResponseType<OrganizacionPropiaResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OrganizacionPropiaResponse>> Actualizar(
        [FromBody] ActualizarNombreOrganizacionRequest request, CancellationToken ct)
    {
        try
        {
            var organization = await editar.EjecutarAsync(request.Nombre, request.Version!.Value, ct);
            return Ok(Desde(organization));
        }
        catch (OrganizacionSinDuenoException exception)
        { return StatusCode(StatusCodes.Status403Forbidden, new { message = exception.Message }); }
        catch (AccesoNoPermitidoException) { return Forbid(); }
        catch (OrganizacionActualNoEncontradaException exception)
        { return NotFound(new { message = exception.Message }); }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { message = "La organización cambió desde la última consulta. Volvé a cargar los datos antes de guardar." });
        }
        catch (ArgumentException exception)
        { return BadRequest(new { message = exception.Message }); }
    }

    [HttpGet("solicitudes-baja")]
    [ProducesResponseType<IReadOnlyList<SolicitudBajaResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListarSolicitudesBaja(CancellationToken ct)
    {
        try
        {
            var requests = await bajas.ListarPropiasAsync(ct);
            return Ok(requests.Select(SolicitudBajaResponse.Desde).ToArray());
        }
        catch (AccesoNoPermitidoException) { return Forbid(); }
    }

    [HttpPost("solicitudes-baja")]
    [ProducesResponseType<SolicitudBajaResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SolicitarBaja([FromBody] SolicitarBajaRequest request, CancellationToken ct)
    {
        try
        {
            var result = await bajas.SolicitarPropiaAsync(request.Motivo, ct);
            return Created("/api/organizacion/actual/solicitudes-baja", SolicitudBajaResponse.Desde(result));
        }
        catch (SolicitudBajaPendienteException error) { return Conflict(new { message = error.Message }); }
        catch (AccesoNoPermitidoException) { return Forbid(); }
        catch (OrganizacionActualNoEncontradaException error) { return NotFound(new { message = error.Message }); }
        catch (ArgumentException error) { return BadRequest(new { message = error.Message }); }
    }

    private static OrganizacionPropiaResponse Desde(OrganizacionPropiaActual organization)
        => new(organization.Id, organization.Nombre, organization.Version, organization.Estado.ToString(),
            organization.EsPropietario, organization.TieneDuenoRegistrado);
}
