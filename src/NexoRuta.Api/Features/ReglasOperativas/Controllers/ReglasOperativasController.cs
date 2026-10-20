using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Api.Features.ReglasOperativas.Contracts;
using NexoRuta.Application.ReglasOperativas.UseCases;
using NexoRuta.Application.ZonasCobertura.Exceptions;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.ReglasOperativas;
using ReglasOperativasDomain = NexoRuta.Domain.ReglasOperativas.ReglasOperativas;

namespace NexoRuta.Api.Features.ReglasOperativas.Controllers;

[ApiController]
[Authorize(Policy = "organizacion")]
[Authorize(Policy = CatalogoPermisos.ConfiguracionGestionar)]
[Route(ReglasOperativasApiContract.BasePath)]
[Produces("application/json")]
[ProducesResponseType<ReglasOperativasErrorResponse>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ReglasOperativasErrorResponse>(StatusCodes.Status403Forbidden)]
public sealed class ReglasOperativasController(
    ConsultarReglasOperativasUseCase consultar,
    CrearBorradorReglasOperativasUseCase crearBorrador,
    EditarBorradorReglasOperativasUseCase editarBorrador,
    PublicarBorradorReglasOperativasUseCase publicarBorrador) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<ReglasOperativasEstadoResponse>(StatusCodes.Status200OK)]
    public Task<IActionResult> ObtenerEstado(CancellationToken ct) => Ejecutar(async () =>
    {
        var state = await consultar.EjecutarAsync(ct);
        return Ok(ReglasOperativasContractMapper.Estado(state));
    });

    [HttpPost("borrador")]
    [ProducesResponseType<VersionReglasOperativasResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ReglasOperativasErrorResponse>(StatusCodes.Status409Conflict)]
    public Task<IActionResult> CrearBorrador(CancellationToken ct) => Ejecutar(async () =>
    {
        var draft = await crearBorrador.EjecutarAsync(ct);
        var location = $"{Request.PathBase}/{ReglasOperativasApiContract.BasePath}/borrador";
        return Created(location, ReglasOperativasContractMapper.Detalle(draft));
    });

    [HttpGet("borrador")]
    [ProducesResponseType<VersionReglasOperativasResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ReglasOperativasErrorResponse>(StatusCodes.Status404NotFound)]
    public Task<IActionResult> ObtenerBorrador(CancellationToken ct) => Ejecutar(async () =>
    {
        var draft = await consultar.ObtenerBorradorAsync(ct);
        return draft is null
            ? NotFound(Error(ReglasOperativasApiErrors.DraftNotFound, "No existe un borrador para este operador."))
            : Ok(ReglasOperativasContractMapper.Detalle(draft));
    });

    [HttpPut("borrador")]
    [RequestSizeLimit(ReglasOperativasApiLimits.MaxRequestBytes)]
    [ProducesResponseType<VersionReglasOperativasResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ReglasOperativasErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ReglasOperativasErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ReglasOperativasErrorResponse>(StatusCodes.Status409Conflict)]
    public Task<IActionResult> ReemplazarBorrador(
        [FromBody] ReemplazarBorradorReglasOperativasRequest request, CancellationToken ct) => Ejecutar(async () =>
    {
        var updated = await editarBorrador.EjecutarAsync(request.VersionConcurrencia,
            ReglasOperativasContractMapper.Configuracion(request), ct);
        return Ok(ReglasOperativasContractMapper.Detalle(updated));
    });

    [HttpPost("borrador/publicar")]
    [ProducesResponseType<VersionReglasOperativasResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ReglasOperativasErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ReglasOperativasErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ReglasOperativasErrorResponse>(StatusCodes.Status409Conflict)]
    public Task<IActionResult> PublicarBorrador(
        [FromBody] PublicarBorradorReglasOperativasRequest request, CancellationToken ct) => Ejecutar(async () =>
    {
        var published = await publicarBorrador.EjecutarAsync(request.VersionConcurrencia, ct);
        return Ok(ReglasOperativasContractMapper.Detalle(published));
    });

    [HttpGet("publicaciones")]
    [ProducesResponseType<PaginaPublicacionesReglasOperativasResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ReglasOperativasErrorResponse>(StatusCodes.Status400BadRequest)]
    public Task<IActionResult> ListarPublicaciones(
        [FromQuery, Range(1, int.MaxValue)] int pagina = 1,
        [FromQuery, Range(1, ReglasOperativasApiLimits.MaxPageSize)] int tamanoPagina = ReglasOperativasApiLimits.DefaultPageSize,
        CancellationToken ct = default) => Ejecutar(async () =>
    {
        var (items, total) = await consultar.ListarPublicacionesAsync(pagina, tamanoPagina, ct);
        return Ok(new PaginaPublicacionesReglasOperativasResponse(
            items.Select(ReglasOperativasContractMapper.Resumen).ToArray(), pagina, tamanoPagina, total));
    });

    [HttpGet("publicaciones/{versionId:guid}")]
    [ProducesResponseType<VersionReglasOperativasResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ReglasOperativasErrorResponse>(StatusCodes.Status404NotFound)]
    public Task<IActionResult> ObtenerPublicacion(Guid versionId, CancellationToken ct) => Ejecutar(async () =>
    {
        var publication = await consultar.ObtenerPublicacionAsync(versionId, ct);
        return publication is null
            ? NotFound(Error(ReglasOperativasApiErrors.PublicationNotFound, "No se encontró la publicación."))
            : Ok(ReglasOperativasContractMapper.Detalle(publication));
    });

    private async Task<IActionResult> Ejecutar(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (AccesoCoberturaNoPermitidoException) { return Forbid(); }
        catch (ConflictoReglasOperativasException error)
        {
            return Conflict(Error(error.Code, error.Message));
        }
        catch (ReglasOperativasIncompletasException error)
        {
            return BadRequest(new ReglasOperativasErrorResponse(error.Code,
                "Falta completar reglas obligatorias antes de publicar.",
                error.CamposIncompletos.ToDictionary(ReglasOperativasContractMapper.CampoApi,
                    field => new[] { CampoIncompleto(field) }, StringComparer.Ordinal)));
        }
        catch (ArgumentOutOfRangeException error)
        {
            return BadRequest(Error(ReglasOperativasApiErrors.InvalidRequest,
                "Uno o más valores están fuera del rango permitido.",
                ReglasOperativasContractMapper.CampoApi(error.ParamName), error.Message));
        }
        catch (ArgumentException error)
        {
            return BadRequest(Error(ReglasOperativasApiErrors.InvalidConfiguration,
                "La configuración del borrador no es válida.",
                ReglasOperativasContractMapper.CampoApi(error.ParamName), error.Message));
        }
    }

    private static ReglasOperativasErrorResponse Error(string code, string message,
        string? field = null, string? fieldMessage = null)
        => new(code, message, field is null || fieldMessage is null
            ? null
            : new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [fieldMessage] });

    private static string CampoIncompleto(string field) => field switch
    {
        nameof(VersionReglasOperativas.MaxIntentosEntrega) =>
            $"Indique un valor entre 1 y {ReglasOperativasDomain.MaxDeliveryAttempts}.",
        nameof(VersionReglasOperativas.IntervaloEntreIntentosMinutos) =>
            $"Indique una espera entre 1 y {ReglasOperativasDomain.MaxDurationMinutes} minutos.",
        nameof(VersionReglasOperativas.PoliticaDevolucionAlAgotarIntentos) => "Seleccione una política de devolución.",
        nameof(VersionReglasOperativas.PlazoEstandarMinutos) =>
            $"Indique un plazo Estándar entre 1 y {ReglasOperativasDomain.MaxDurationMinutes} minutos.",
        nameof(VersionReglasOperativas.PlazoUrgenteMinutos) =>
            $"Indique un plazo Urgente entre 1 y {ReglasOperativasDomain.MaxDurationMinutes} minutos.",
        nameof(VersionReglasOperativas.RequisitosPrueba) => "Seleccione al menos un requisito de prueba.",
        nameof(VersionReglasOperativas.Motivos) => "Agregue al menos un motivo disponible.",
        _ => "Complete este dato obligatorio."
    };
}
