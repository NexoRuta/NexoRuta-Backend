using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Api.Features.Tarifas.Contracts;
using NexoRuta.Application.Tarifas.UseCases;
using NexoRuta.Application.ZonasCobertura.Exceptions;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Tarifas;

namespace NexoRuta.Api.Features.Tarifas.Controllers;

[ApiController]
[Authorize(Policy = "organizacion")]
[Authorize(Policy = CatalogoPermisos.ConfiguracionGestionar)]
[Route(TarifarioApiContract.BasePath)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class TarifarioController(
    ConsultarTarifarioUseCase consultar,
    CrearBorradorTarifarioUseCase crearBorrador,
    EditarBorradorTarifarioUseCase editarBorrador,
    PublicarBorradorTarifarioUseCase publicarBorrador,
    SimularTarifaUseCase simular) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<TarifarioEstadoResponse>(StatusCodes.Status200OK)]
    public Task<IActionResult> ObtenerEstado(CancellationToken ct) => Ejecutar(async () =>
    {
        var query = await consultar.EjecutarAsync(ct);
        return Ok(TarifarioContractMapper.Estado(query));
    });

    [HttpGet("publicaciones")]
    [ProducesResponseType<PaginaPublicacionesTarifarioResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public Task<IActionResult> ListarPublicaciones(
        [FromQuery, Range(1, int.MaxValue)] int pagina = 1,
        [FromQuery, Range(1, TarifarioApiLimits.MaxPageSize)] int tamanoPagina = 20,
        CancellationToken ct = default) => Ejecutar(async () =>
    {
        var (items, total) = await consultar.ListarPublicacionesAsync(pagina, tamanoPagina, ct);
        return Ok(new PaginaPublicacionesTarifarioResponse(items.Select(TarifarioContractMapper.Resumen).ToArray(),
            pagina, tamanoPagina, total));
    });

    [HttpGet("publicaciones/{versionId:guid}")]
    [ProducesResponseType<VersionTarifarioResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> ObtenerPublicacion(Guid versionId, CancellationToken ct) => Ejecutar(async () =>
    {
        var version = await consultar.ObtenerPublicacionAsync(versionId, ct);
        return version is null ? NotFound(Error("tariff.publication_not_found", "No se encontró la publicación."))
            : Ok(TarifarioContractMapper.Detalle(version));
    });

    [HttpPost("borrador")]
    [ProducesResponseType<VersionTarifarioResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> CrearBorrador(CancellationToken ct) => Ejecutar(async () =>
    {
        var draft = await crearBorrador.EjecutarAsync(ct);
        return Created($"{Request.PathBase}/{TarifarioApiContract.BasePath}/borrador",
            TarifarioContractMapper.Detalle(draft));
    });

    [HttpGet("borrador")]
    [ProducesResponseType<VersionTarifarioResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> ObtenerBorrador(CancellationToken ct) => Ejecutar(async () =>
    {
        var query = await consultar.EjecutarAsync(ct);
        var draft = query.Versiones.SingleOrDefault(x => x.Estado == EstadoVersionTarifario.Borrador);
        return draft is null ? NotFound(Error("tariff.draft_not_found", "No existe un borrador para este operador."))
            : Ok(TarifarioContractMapper.Detalle(draft));
    });

    [HttpPut("borrador/tarifas")]
    [RequestSizeLimit(TarifarioApiLimits.MaxRequestBytes)]
    [ProducesResponseType<VersionTarifarioResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> ReemplazarTarifas([FromBody] ReemplazarTarifasBorradorRequest request,
        CancellationToken ct) => Ejecutar(async () =>
    {
        var rates = request.Tarifas;
        if (rates.Any(x => x is null)) return BadRequest(Error("tariff.invalid_rate", "La configuración contiene una tarifa vacía."));
        var configuration = rates.Select(rate => new TarifaZonaModalidadConfiguracion(
            rate.ZonaId,
            TarifarioContractMapper.Modalidad(rate.Modalidad),
            rate.ImporteBaseUyu,
            rate.PrecioPorKilogramoUyu,
            rate.PrecioPorMetroCubicoUyu,
            MapAdjustments(rate.Ajustes!))).ToArray();
        var updated = await editarBorrador.EjecutarAsync(request.VersionConcurrencia, configuration, ct);
        return Ok(TarifarioContractMapper.Detalle(updated));
    });

    [HttpPost("borrador/publicar")]
    [ProducesResponseType<VersionTarifarioResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Publicar([FromBody] PublicarBorradorTarifarioRequest request,
        CancellationToken ct) => Ejecutar(async () =>
    {
        var published = await publicarBorrador.EjecutarAsync(request.VersionConcurrencia, ct);
        return Ok(TarifarioContractMapper.Detalle(published));
    });

    [HttpPost("simulaciones")]
    [RequestSizeLimit(TarifarioApiLimits.MaxRequestBytes)]
    [ProducesResponseType<SimulacionTarifaResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Simular([FromBody] SimularTarifaRequest request, CancellationToken ct)
        => Ejecutar(async () =>
        {
            var parcels = request.Bultos!;
            if (parcels.Any(x => x is null)) return BadRequest(Error("tariff.invalid_parcel", "La simulación contiene un bulto vacío."));
            var calculation = await simular.EjecutarAsync(new SolicitudSimulacionTarifario(
                request.VersionTarifarioId,
                request.ZonaId,
                TarifarioContractMapper.Modalidad(request.Modalidad),
                parcels.Select(parcel => new BultoCalculo(parcel.PesoGramos,
                    parcel.LargoCentimetros, parcel.AnchoCentimetros,
                    parcel.AltoCentimetros)).ToArray()), ct);
            return Ok(TarifarioContractMapper.Simulacion(calculation));
        });

    private static IReadOnlyList<AjusteTarifarioConfiguracion> MapAdjustments(
        IReadOnlyCollection<AjusteTarifarioRequest> adjustments)
    {
        if (adjustments.Any(x => x is null))
            throw new ArgumentException("La configuración contiene un ajuste vacío.");
        return adjustments.Select(x => new AjusteTarifarioConfiguracion(x.Nombre!,
            TarifarioContractMapper.Tipo(x.Tipo), x.ImporteFijoUyu, x.Porcentaje)).ToArray();
    }

    private async Task<IActionResult> Ejecutar(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (AccesoCoberturaNoPermitidoException) { return Forbid(); }
        catch (ConflictoTarifarioException error) { return Conflict(Error(error.Code, error.Message)); }
        catch (TarifaNoConfiguradaException error)
        {
            return NotFound(Error(error.Code, "No existe una tarifa para la zona y modalidad solicitadas."));
        }
        catch (KeyNotFoundException error) { return NotFound(Error("resource.not_found", error.Message)); }
        catch (ArgumentException error) { return BadRequest(Error("request.invalid", error.Message)); }
        catch (OverflowException) { return BadRequest(Error("tariff.calculation_overflow", "Los valores exceden el rango admitido para simular la tarifa.")); }
    }

    private static object Error(string code, string message) => new { code, message };
}
