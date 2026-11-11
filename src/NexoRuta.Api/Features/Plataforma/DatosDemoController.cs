using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Api.Features.Plataforma.Contracts;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.Application.Reparto.Dtos;

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
    public async Task<IActionResult> Sembrar(CancellationToken ct)
    {
        if (HttpContext.Items[typeof(ContextoPlataforma)] is not ContextoPlataforma actor)
            return Forbid();

        var (_, estado) = await seeder.SembrarAsync(actor, ct);
        return Ok(DatosDemoStatusResponse.Desde(estado));
    }

    [HttpPost("load-fixture")]
    [ProducesResponseType<SembrarFixtureCargaResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SembrarFixtureCarga(SembrarFixtureCargaRequest request, CancellationToken ct)
    {
        if (HttpContext.Items[typeof(ContextoPlataforma)] is not ContextoPlataforma actor)
            return Forbid();
        try
        {
            var cuentas = await seeder.SembrarFixtureCargaAsync(actor,
                request.Cuentas.Select(x => new CredencialFixtureCarga(x.Email, x.Password)).ToArray(), ct);
            return Ok(new SembrarFixtureCargaResponse(cuentas.Select(x =>
                new CuentaFixtureCargaResponse(x.Email, x.JornadaId, x.Operaciones)).ToArray()));
        }
        catch (ArgumentException error)
        {
            return BadRequest(new { message = error.Message });
        }
    }
}
