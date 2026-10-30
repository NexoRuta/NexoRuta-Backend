
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Api.Features.Envios.Contracts.Requests;
using NexoRuta.Api.Features.Envios.Contracts.Responses;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Envios.Commands;
using NexoRuta.Application.Envios.UseCases;
using NexoRuta.Application.Envios.Exceptions;
using NexoRuta.Application.Envios.Importacion;
using NexoRuta.Application.Reparto.Exceptions;
using NexoRuta.Domain.Administracion;
using EnvioCreado = NexoRuta.Api.Features.Envios.Contracts.Responses.EnvioCreado;
using EnvioDetalle = NexoRuta.Api.Features.Envios.Contracts.Responses.EnvioDetalle;

namespace NexoRuta.Api.Features.Envios.Controllers;

[ApiController]
[Authorize]
[Route("api")]
public sealed class EnviosController(
    CrearEnvioUseCase crearEnvio,
    ListarEnviosUseCase listarEnvios,
    ImportarEnviosUseCase importarEnvios) : ControllerBase
{
    [HttpPost("envios")]
    [Authorize(Policy = "envios.crear")]
    [Authorize(Policy = nameof(TipoAccesoUsuario.Comercio))]
    [ProducesResponseType<EnvioCreado>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CreateShipment(
        [FromBody] CrearEnvioRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var created = await crearEnvio.EjecutarAsync(
                new CrearEnvioCommand(
                    OperadorId: request.OperadorId!.Value,
                    DestinatarioNombre: request.DestinatarioNombre,
                    DestinatarioEmail: request.DestinatarioEmail,
                    Direccion: request.Direccion,
                    CodigoBulto: request.CodigoBulto,
                    PesoGramos: request.PesoGramos,
                    LargoCentimetros: request.LargoCentimetros,
                    AnchoCentimetros: request.AnchoCentimetros,
                    AltoCentimetros: request.AltoCentimetros),
                cancellationToken);

            return Created("/api/envios", EnvioCreado.Desde(created));
        }
        catch (Exception exception) when (exception is ReglasEnvioNoDisponiblesException or ConflictoRepartoException)
        { return Conflict(new { message = exception.Message }); }
        catch (OperadorNoEncontradoException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
        catch (OrganizacionSuspendidaException exception)
        {
            return Conflict(new { message = exception.Message });
        }
        catch (AccesoNoPermitidoException exception)
        {
            return Problem(
                exception.Message,
                statusCode: StatusCodes.Status403Forbidden);
        }
        catch (AccesoActualNoDisponibleException exception)
        {
            return Problem(
                exception.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    [HttpPost("envios/importaciones")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(CsvEnviosParser.MaximumFileBytes + 32 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = CsvEnviosParser.MaximumFileBytes + 32 * 1024,
        MultipartHeadersLengthLimit = 16 * 1024, ValueLengthLimit = 1024)]
    [Authorize(Policy = "envios.crear")]
    [Authorize(Policy = nameof(TipoAccesoUsuario.Comercio))]
    [ProducesResponseType<ImportacionEnviosResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ImportacionEnviosResponse>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ImportacionEnviosResponse>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ErrorImportacionEnviosResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ErrorImportacionEnviosResponse>(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Importar([FromForm] ImportarEnviosRequest request, CancellationToken cancellationToken)
    {
        if (request.Archivo is null)
            return BadRequest(new ErrorImportacionEnviosResponse("Archivo", "FileRequired", "Seleccioná un archivo CSV."));
        if (request.OperadorId is null || request.OperadorId == Guid.Empty)
            return BadRequest(new ErrorImportacionEnviosResponse("Validacion", "OperatorRequired", "Seleccioná un operador para el archivo."));
        if (request.Archivo.Length > CsvEnviosParser.MaximumFileBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                new ErrorImportacionEnviosResponse("Archivo", "FileTooLarge", "El archivo supera el límite de 5 MiB."));

        await using var content = new MemoryStream(checked((int)request.Archivo.Length));
        await request.Archivo.CopyToAsync(content, cancellationToken);
        try
        {
            var result = await importarEnvios.EjecutarAsync(content.ToArray(), request.OperadorId.Value, cancellationToken);
            var response = ImportacionEnviosResponse.Desde(result);
            if (result.Completada)
                return Ok(response);
            return result.TipoRechazo == TipoRechazoImportacionEnvios.Conflicto
                ? Conflict(response)
                : UnprocessableEntity(response);
        }
        catch (CsvEnviosFileException error)
        {
            var status = error.Code is CsvEnviosFileErrorCode.FileTooLarge or CsvEnviosFileErrorCode.TooManyRecords
                ? StatusCodes.Status413PayloadTooLarge
                : StatusCodes.Status400BadRequest;
            return StatusCode(status, new ErrorImportacionEnviosResponse("Archivo", error.Code.ToString(), error.Message));
        }
        catch (AccesoNoPermitidoException error)
        {
            return Problem(error.Message, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (AccesoActualNoDisponibleException error)
        {
            return Problem(error.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (ArgumentException error)
        {
            return BadRequest(new ErrorImportacionEnviosResponse("Validacion", "InvalidRequest", error.Message));
        }
    }

    [HttpGet("envios")]
    [Authorize(Policy = "envios.consultar")]
    [ProducesResponseType<IReadOnlyList<EnvioDetalle>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetShipments(
        CancellationToken cancellationToken)
    {
        try
        {
            var envios = await listarEnvios.EjecutarAsync(cancellationToken);

            return Ok(envios.Select(EnvioDetalle.Desde).ToArray());
        }
        catch (AccesoActualNoDisponibleException exception)
        {
            return Problem(
                exception.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
