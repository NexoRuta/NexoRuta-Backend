using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.Application.Reparto.Exceptions;
using NexoRuta.Application.Reparto.UseCases;

namespace NexoRuta.Api.Features.Reparto.Controllers;

[ApiController]
[Authorize]
[Route("api/repartidores")]
public sealed class RepartidoresController(GestionarRepartidoresUseCase casos) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "usuarios.gestionar")]
    [ProducesResponseType<RepartidorResponse[]>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Listar(CancellationToken ct)
    {
        try { return Ok((await casos.ListarAsync(ct)).Select(RepartidorResponse.Desde).ToArray()); }
        catch (RepartoNoPermitidoException) { return Forbid(); }
    }

    [HttpPut("{accesoId:guid}")]
    [Authorize(Policy = "usuarios.gestionar")]
    [ProducesResponseType<RepartidorResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Guardar(Guid accesoId, GuardarRepartidorRequest request, CancellationToken ct)
    {
        try
        {
            var datos = await casos.GuardarAsync(accesoId, request.Nombre, request.Activo, request.Version, ct);
            return datos is null ? NotFound() : Ok(RepartidorResponse.Desde(datos));
        }
        catch (RepartoNoPermitidoException) { return Forbid(); }
        catch (ConflictoRepartoException ex) { return Problem(ex.Message, statusCode: 409); }
        catch (ArgumentException ex) { return Problem(ex.Message, statusCode: 400); }
    }

    [HttpGet("/api/mobile/perfil")]
    [Authorize(Policy = "reparto.ejecutar")]
    [ProducesResponseType<RepartidorResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Perfil(CancellationToken ct)
    {
        try
        {
            var perfil = await casos.PerfilAsync(ct);
            return perfil is null ? Forbid() : Ok(RepartidorResponse.Desde(perfil));
        }
        catch (RepartoNoPermitidoException) { return Forbid(); }
    }
}
