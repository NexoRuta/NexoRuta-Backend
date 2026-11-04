using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Reparto.Dtos;
namespace NexoRuta.IntegrationTests.Fixtures;
internal static class RecepcionTestData
{
    internal static async Task RecibirInicialAsync(IntegrationDatabase data, HttpClient client, Guid envio)
    {
        await using var db = data.CreateDbContext();
        var bultos = await db.Bultos.AsNoTracking().Where(b => b.EnvioId == envio).ToArrayAsync();
        Assert.NotEmpty(bultos);
        foreach (var b in bultos)
        {
            var version = await db.Envios.Where(e=>e.Id==envio).Select(e=>e.Version).SingleAsync();
            using var r = await client.PostAsJsonAsync($"/api/deposito/bultos/{b.Id}/recepcion", new CotejoBultoDatos(
                Guid.NewGuid(),version,b.Version,true,b.PesoGramos,b.LargoCentimetros,b.AnchoCentimetros,b.AltoCentimetros,""));
            Assert.True(r.StatusCode == HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        }
    }
}
