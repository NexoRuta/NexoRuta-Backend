
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Application.Administracion.Dtos;
using NexoRuta.Application.Administracion.UseCases;
using NexoRuta.Api.Contracts.Usuarios;
using NexoRuta.Application.Administracion.Exceptions;
namespace NexoRuta.Api.Controllers;

[ApiController]
[Route("api/usuarios")]
[Authorize(Policy = "usuarios.gestionar")]
public sealed class UsuariosController(
    ListarUsuariosUseCase listarUsuarios,
    CrearUsuarioUseCase crearUsuario,
    CambiarEstadoUsuarioUseCase cambiarEstadoUsuario) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(
        typeof(IReadOnlyList<UsuarioOrganizacionResumen>),
        StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<UsuarioOrganizacionResumen>>>
        Listar(CancellationToken cancellationToken)
    {
        var usuarios = await listarUsuarios.EjecutarAsync(
            cancellationToken);

        return Ok(usuarios);
    }


    [HttpPost]
    [ProducesResponseType(
        typeof(UsuarioOrganizacionResumen),
        StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UsuarioOrganizacionResumen>>
        Crear(
            [FromBody] CrearUsuarioRequest request,
            CancellationToken ct)
    {
        try
        {
            var creado = await crearUsuario.EjecutarAsync(
                request.Email,
                request.Password,
                request.RolId,
                ct);

            return StatusCode(
                StatusCodes.Status201Created,
                creado);
        }
        catch (CorreoYaRegistradoException)
        {
            return Conflict(new
            {
                message = "El correo ya está registrado."
            });
        }
        catch (AccesoNoPermitidoException)
        {
            return Forbid();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }


    [HttpPatch("{usuarioId:guid}/estado")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CambiarEstado(
        Guid usuarioId,
        [FromBody] CambiarEstadoUsuarioRequest request,
        CancellationToken ct)
    {
        try
        {
            var actualizado =
                await cambiarEstadoUsuario.EjecutarAsync(
                    usuarioId,
                    request.Activo!.Value,
                    ct);

            return actualizado ? NoContent() : NotFound();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new
            {
                message = "El estado del usuario cambió durante la operación. Volvé a consultar e intentá nuevamente."
            });
        }
        catch (AccesoNoPermitidoException)
        {
            return Forbid();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }


}
