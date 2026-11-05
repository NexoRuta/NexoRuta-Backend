using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Application.Notificaciones.Repositories;
namespace NexoRuta.Api.Features.Envios.Controllers;

[ApiController, Authorize(Policy="envios.consultar"), Route("api/avisos-comercio")]
public sealed class AvisosComercioController(IAvisosComercioRepository avisos) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<AvisoComercioDatos>> Listar(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return await avisos.ListarAsync(ct);
    }
}