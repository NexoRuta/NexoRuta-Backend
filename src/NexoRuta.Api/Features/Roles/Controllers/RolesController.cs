using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Api.Features.Roles.Contracts;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.UseCases;

namespace NexoRuta.Api.Features.Roles.Controllers;

[ApiController]
[Authorize]
[Route("api/roles")]
public sealed class RolesController(GestionarRolesUseCase roles) : ControllerBase
{
    [HttpGet, Authorize(Policy = "configuracion.gestionar")]
    public Task<IActionResult> List(CancellationToken ct) => Execute(async () => Ok(await roles.ListarAsync(ct)));

    [HttpGet("permisos"), Authorize(Policy = "configuracion.gestionar")]
    public Task<IActionResult> Permissions(CancellationToken ct) => Execute(async () => Ok(await roles.PermisosAsync(ct)));

    [HttpPost, Authorize(Policy = "configuracion.gestionar")]
    public Task<IActionResult> Create(CrearRolRequest request, CancellationToken ct) => Execute(async () =>
    {
        var id = await roles.CrearAsync(request.Codigo, request.Nombre, ct);
        return Created($"/api/roles/{id}", new { id });
    });

    [HttpPut("{id:guid}"), Authorize(Policy = "configuracion.gestionar")]
    public Task<IActionResult> Rename(Guid id, ActualizarRolRequest request, CancellationToken ct)
        => Execute(async () => { await roles.RenombrarAsync(id, request.Nombre, ct); return NoContent(); });

    [HttpDelete("{id:guid}"), Authorize(Policy = "configuracion.gestionar")]
    public Task<IActionResult> Delete(Guid id, CancellationToken ct)
        => Execute(async () => { await roles.EliminarAsync(id, ct); return NoContent(); });

    [HttpPut("{id:guid}/permisos"), Authorize(Policy = "configuracion.gestionar")]
    public Task<IActionResult> Grants(Guid id, ActualizarPermisosRolRequest request, CancellationToken ct)
        => Execute(async () => { await roles.ActualizarPermisosAsync(id, request.Permisos, ct); return NoContent(); });

    [HttpPut("accesos/{accesoId:guid}"), Authorize(Policy = "usuarios.gestionar")]
    public Task<IActionResult> Assign(Guid accesoId, AsignarRolRequest request, CancellationToken ct)
        => Execute(async () => { await roles.AsignarAsync(accesoId, request.RolId, ct); return NoContent(); });

    private async Task<IActionResult> Execute(Func<Task<IActionResult>> operation)
    {
        try { return await operation(); }
        catch (AccesoNoPermitidoException) { return Forbid(); }
        catch (ArgumentException error) { return BadRequest(new { message = error.Message }); }
        catch (InvalidOperationException error) { return Conflict(new { message = error.Message }); }
        catch (DbUpdateException) { return Conflict(new { message = "Role data conflicts with current organization configuration." }); }
    }
}
