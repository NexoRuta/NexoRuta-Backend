using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.IntegrationTests.Fixtures;
using static NexoRuta.IntegrationTests.Fixtures.RepartoHttpData;
namespace NexoRuta.IntegrationTests.Reparto;
public sealed partial class OcupacionFlotaHttpTests
{
    [Fact]
    public async Task FleetContractAndCommerceAuthorizationAreExplicit()
    {
        var data = await postgres.CreateDatabaseAsync(); using var factory = new IntegrationApiFactory(data, environment: "Development"); using var client = factory.CreateHttpClient();
        var p = await Preparar(data, client);
        var json = await client.GetStringAsync("/openapi/v1.json");
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("paths").TryGetProperty("/api/reparto/flota", out _));
        Assert.True(doc.RootElement.GetProperty("components").GetProperty("schemas").TryGetProperty("OcupacionFlotaResponse", out _));
        var capture = Environment.GetEnvironmentVariable("NEXORUTA_CAPTURE_OPENAPI");
        if (!string.IsNullOrEmpty(capture)) await File.WriteAllTextAsync(capture, json);
        await using var db = data.CreateDbContext();
        await client.SetBearerForAccessAsync(data, (await db.AccesosUsuario.SingleAsync(a => a.ComercioId != null)).Id);
        using var get = await client.GetAsync("/api/reparto/flota"); Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
        using var post = await client.PostAsJsonAsync($"/api/reparto/flota/{p.VehiculoId}/retorno", new ComandoFlotaRequest(Guid.NewGuid(), Guid.NewGuid(), 0, "Sin permiso."));
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
    }
}
