using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Infrastructure.Operacion;

namespace NexoRuta.Api.Features.Operacion;

[ApiController, Route("api/operacion"), Authorize(Policy = "envios.consultar")]
public sealed class OperationalController(IUsuarioActual usuario, OperationalReadStore read) : ControllerBase
{
    [HttpGet("fotografia")]
    public async Task<IActionResult> Snapshot(CancellationToken ct)
    {
        var actor = await usuario.ObtenerAsync(ct);
        if (actor.ComercioId is not null || actor.OperadorId is not { } op) return Forbid();
        return Ok(await read.SnapshotAsync(op, ct));
    }

}
