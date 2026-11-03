using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Application.Administracion.Dtos;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Domain.Administracion;
using NexoRuta.Api.Features.Comercios.Contracts.Responses;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Envios.Repositories;

namespace NexoRuta.Api.Features.Comercios.Controllers;

[ApiController]
[Authorize(Policy = nameof(TipoAccesoUsuario.Comercio))]
[Route("api/operadores")]
public sealed class OperadoresDisponiblesController(IOperadoresRepository operadores,
    IServiciosEnvioRepository servicios, IEnviosRepository envios) : ControllerBase
{
    [HttpGet("alternativas-envio")]
    [Authorize(Policy = "envios.crear")]
    [ProducesResponseType<IReadOnlyList<AlternativaOperadorEnvioResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Alternativas(
        [FromQuery, System.ComponentModel.DataAnnotations.Required] double? latitud,
        [FromQuery, System.ComponentModel.DataAnnotations.Required] double? longitud,
        [FromQuery] Guid? excluirOperadorId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (latitud is not { } lat || longitud is not { } lon || !double.IsFinite(lat) || !double.IsFinite(lon)
            || lat is < -90 or > 90 || lon is < -180 or > 180)
            return BadRequest(new { message = "Indicá una ubicación válida para consultar alternativas." });
        try
        {
            var alternatives = new List<AlternativaOperadorEnvioResponse>();
            // ponytail: one authorized rules/offers lookup per active operator; batch only if catalog scale requires it.
            foreach (var candidate in await operadores.ListarAsync(ct))
            {
                if (candidate.OperadorId == excluirOperadorId) continue;
                if (await envios.ObtenerVersionReglasParaAdmisionAsync(candidate.OperadorId, ct) is null) continue;
                var offers = await servicios.ListarAsync(candidate.OperadorId, lat, lon, ct);
                if (offers.Count > 0)
                    alternatives.Add(new(candidate.OperadorId, candidate.Nombre,
                        offers.Select(s => s.Modalidad).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()));
            }
            return Ok(alternatives);
        }
        catch (Exception error) when (error is OrganizacionSuspendidaException or ArgumentException)
        {
            // A database availability error aborts the request transaction: never continue with a partial catalog.
            return Problem("Cambió la disponibilidad de un operador. Conservá el destino e intentá nuevamente.",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["code"] = "operator_alternatives_changed" });
        }
    }

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<OperadorDisponible>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Listar(CancellationToken cancellationToken)
    {
        return Ok(await operadores.ListarAsync(cancellationToken));
    }

    [HttpGet("importacion-envios")]
    [Authorize(Policy = "envios.crear")]
    [ProducesResponseType<IReadOnlyList<OperadorDisponibleParaImportacion>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListarParaImportacion(CancellationToken cancellationToken)
        => Ok(await operadores.ListarParaImportacionAsync(cancellationToken));
}
