using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Api.Features.Plataforma.Contracts;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Api.Features.Plataforma;

[ApiController]
[Route("api/admin/demo-data")]
[Authorize(Policy = RolPlataforma.GestionarOrganizaciones)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public sealed class DatosDemoController(DatosDemoSeeder seeder) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<DatosDemoStatusResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Estado(CancellationToken ct)
        => Ok(DatosDemoStatusResponse.Desde(await seeder.ObtenerEstadoAsync(ct)));

    [HttpPost]
    [ProducesResponseType<DatosDemoStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<DatosDemoStatusResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Sembrar(CancellationToken ct)
    {
        if (HttpContext.Items[typeof(ContextoPlataforma)] is not ContextoPlataforma actor)
            return Forbid();

        var (yaEstabaSembrado, estado) = await seeder.SembrarAsync(actor, ct);
        var response = DatosDemoStatusResponse.Desde(estado);
        return yaEstabaSembrado ? Conflict(response) : Ok(response);
    }
}
