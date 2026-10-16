using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Api.Features.Usuarios.Contracts;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Domain.Administracion;

namespace NexoRuta.Api.Features.Usuarios.Controllers;

[ApiController, Authorize, Route("api/alta-propia")]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[ProducesResponseType(StatusCodes.Status400BadRequest)]
[ProducesResponseType(StatusCodes.Status409Conflict)]
public sealed class AltaPropiaController(IAltaPropiaRepository organizaciones) : ControllerBase
{
    [HttpPost("comercios")]
    [ProducesResponseType<AltaOrganizacion>(StatusCodes.Status201Created)]
    public Task<IActionResult> Comercio(AltaPropiaRequest request, CancellationToken ct) => Crear(TipoAccesoUsuario.Comercio, request, ct);

    [HttpPost("operadores")]
    [ProducesResponseType<AltaOrganizacion>(StatusCodes.Status201Created)]
    public Task<IActionResult> Operador(AltaPropiaRequest request, CancellationToken ct) => Crear(TipoAccesoUsuario.Operador, request, ct);

    private async Task<IActionResult> Crear(TipoAccesoUsuario tipo, AltaPropiaRequest request, CancellationToken ct)
    {
        if (HttpContext.Items[typeof(ContextoPlataforma)] is ContextoPlataforma) return Forbid();
        if (HttpContext.Items[typeof(ContextoUsuario)] is ContextoUsuario)
            return Conflict(new { message = "Ya pertenecés a una organización." });
        if (HttpContext.Items[typeof(IdentidadSinOrganizacion)] is not IdentidadSinOrganizacion identidad) return Forbid();
        try { return StatusCode(StatusCodes.Status201Created, await organizaciones.CrearPropiaAsync(identidad.UsuarioId, tipo, request.Nombre, ct)); }
        catch (OrganizacionYaAsignadaException error) { return Conflict(new { message = error.Message }); }
        catch (ArgumentException error) { return BadRequest(new { message = error.Message }); }
    }
}
