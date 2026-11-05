using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NexoRuta.Application.Envios.UseCases;
namespace NexoRuta.Api.Features.Envios.Controllers;
[ApiController, Authorize(Policy="envios.consultar"), Route("api/reportes/operaciones")]
public sealed class ReporteOperacionesController(ListarEnviosUseCase envios) : ControllerBase
{
    [HttpGet, ProducesResponseType<IReadOnlyList<ReporteOperacionesFila>>(200)]
    public async Task<IActionResult> Consultar(CancellationToken ct)
    {
        Response.Headers.CacheControl="no-store";
        var filas=await envios.EjecutarAsync(ct);
        return Ok(filas.GroupBy(e=>new { e.EsRetiro, e.Estado }).Select(g=>new ReporteOperacionesFila(g.Key.EsRetiro ? "Retiro" : "Entrega",g.Key.Estado,g.Count(),g.Sum(e=>e.Bultos.Count),g.Sum(e=>e.Bultos.Count(b=>e.EsRetiro ? b.Custodia=="Comercio" : b.Resultado=="Entregado")),g.Sum(e=>e.Tarifa?.TotalFinalUyu??0m))).ToArray());
    }
}

public sealed record ReporteOperacionesFila(string Tipo,string Estado,int Operaciones,int Bultos,int BultosEntregados,decimal ImporteUyu);
