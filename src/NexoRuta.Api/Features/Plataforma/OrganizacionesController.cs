using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Api.Controllers;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Domain.Administracion;

namespace NexoRuta.Api.Features.Plataforma;

[ApiController]
[Route("api/admin")]
[Authorize(Policy = RolPlataforma.GestionarOrganizaciones)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class OrganizacionesController(IPlataformaRepository plataforma) : ControllerBase
{
    [HttpPost("operadores")]
    [ProducesResponseType<AltaOrganizacion>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> CrearOperador(CrearOrganizacionRequest request, CancellationToken ct)
        => Crear(TipoAccesoUsuario.Operador, "operadores", request, ct);

    [HttpPost("comercios")]
    [ProducesResponseType<AltaOrganizacion>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> CrearComercio(CrearOrganizacionRequest request, CancellationToken ct)
        => Crear(TipoAccesoUsuario.Comercio, "comercios", request, ct);

    [HttpGet("operadores")]
    [ProducesResponseType<PaginaOrganizaciones>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public Task<IActionResult> Operadores([FromQuery] string? estado, [FromQuery] int pagina = 1, [FromQuery] int tamanoPagina = 20, CancellationToken ct = default)
        => Listar(TipoAccesoUsuario.Operador, estado, pagina, tamanoPagina, ct);

    [HttpGet("comercios")]
    [ProducesResponseType<PaginaOrganizaciones>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public Task<IActionResult> Comercios([FromQuery] string? estado, [FromQuery] int pagina = 1, [FromQuery] int tamanoPagina = 20, CancellationToken ct = default)
        => Listar(TipoAccesoUsuario.Comercio, estado, pagina, tamanoPagina, ct);

    [HttpGet("operadores/{id:guid}")]
    [ProducesResponseType<OrganizacionAdministrativa>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Operador(Guid id, CancellationToken ct) => Obtener(TipoAccesoUsuario.Operador, id, ct);

    [HttpGet("comercios/{id:guid}")]
    [ProducesResponseType<OrganizacionAdministrativa>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<IActionResult> Comercio(Guid id, CancellationToken ct) => Obtener(TipoAccesoUsuario.Comercio, id, ct);

    private async Task<IActionResult> Crear(TipoAccesoUsuario tipo, string path, CrearOrganizacionRequest request, CancellationToken ct)
    {
        try
        {
            var result = await plataforma.CrearAsync(tipo, request.Nombre, request.Administrador.Email, request.Administrador.Password, ct);
            return Created($"/api/admin/{path}/{result.Organizacion.Id}", result);
        }
        catch (Exception error) when (error is CorreoYaRegistradoException or CredencialesInvalidasException)
        { return ErrorCuentaHttp.Traducir(error); }
        catch (ArgumentException error) { return BadRequest(new { message = error.Message }); }
    }

    private async Task<IActionResult> Listar(TipoAccesoUsuario tipo, string? estado, int pagina, int tamano, CancellationToken ct)
    {
        EstadoOrganizacion? parsedState = null;
        if (!string.IsNullOrWhiteSpace(estado))
        {
            if (!Enum.TryParse(estado, ignoreCase: false, out EstadoOrganizacion parsed) || !Enum.IsDefined(parsed))
                return BadRequest(new { message = "El filtro estado debe ser Activa o Suspendida." });
            parsedState = parsed;
        }
        try { return Ok(await plataforma.ListarAsync(tipo, parsedState, pagina, tamano, ct)); }
        catch (ArgumentException error) { return BadRequest(new { message = error.Message }); }
    }

    private async Task<IActionResult> Obtener(TipoAccesoUsuario tipo, Guid id, CancellationToken ct)
        => await plataforma.ObtenerAsync(tipo, id, ct) is { } result ? Ok(result) : NotFound();
}
