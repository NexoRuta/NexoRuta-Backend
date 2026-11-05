using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.IntegrationTests.Fixtures;
namespace NexoRuta.IntegrationTests.Reparto;
public sealed partial class OperacionesRepartoHttpTests
{
    private sealed class RoutingBultosControlado : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("api.geoapify.com", request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { results = new[] { new { distance_units = "meters", legs = new[] { new { time = 60d, distance = 100d } } } } }) });
        }
    }
    private static async Task ConfigurarBaseVial(HttpClient client, Guid vehiculo)
    {
        using var baseNueva = await client.PutAsJsonAsync("/api/reparto/base-operativa", new { Version = 0, Direccion = "Base sintética H5", ServicioPredeterminadoMinutos = 1, MargenRetornoMinutos = 1 });
        Assert.Equal(HttpStatusCode.OK, baseNueva.StatusCode);
        using var confirmar = await client.PostAsJsonAsync("/api/reparto/base-operativa/confirmacion", new { Version = 1, Latitud = -34.91, Longitud = -56.21 });
        Assert.Equal(HttpStatusCode.OK, confirmar.StatusCode);
        await ConfigurarPerfilVial(client, vehiculo);
    }
    private static async Task ConfigurarPerfilVial(HttpClient client, Guid vehiculo)
    {
        using var perfil = await client.PutAsJsonAsync($"/api/reparto/perfiles-viales/{vehiculo}", new { Version = 0, Compatibilidad = "DriveConfirmado", ConfirmarCompatibilidad = true, Motivo = "Perfil sintético H5" });
        Assert.Equal(HttpStatusCode.OK, perfil.StatusCode);
    }
    private static async Task VerificarRecorridoBultos(IntegrationDatabase data, HttpClient client, HojaRutaResponse hoja, DateTimeOffset ahora, int secuencia, params Guid[] pendientes)
    {
        var gps = new { Id = Guid.NewGuid(), Secuencia = secuencia, Tipo = "Posicion", OcurridaEn = ahora, Datos = new { Latitud = -34.89, Longitud = -56.19, PrecisionMetros = 5, PosicionEnUtc = ahora } };
        Assert.Equal("Aplicada", (await Aplicar(client, hoja, gps)).Resultado);
        await using var db = data.CreateDbContext();
        var ruta = await db.HojasRuta.AsNoTracking().SingleAsync(h => h.Id == hoja.Id);
        var ocupacion = await db.OcupacionesRuta.AsNoTracking().SingleAsync(o => o.RutaId == hoja.Id);
        using var calcular = await client.PostAsJsonAsync($"/api/rutas/{hoja.Id}/estimaciones/recorrido", new { ComandoId = Guid.NewGuid(), VersionRuta = ruta.Version, OcupacionId = ocupacion.Id });
        Assert.True(calcular.IsSuccessStatusCode, await calcular.Content.ReadAsStringAsync());
        var resultado = (await calcular.Content.ReadFromJsonAsync<EstimacionRutaResponse>())!;
        Assert.Equal("Calculada", resultado.Estado);
        Assert.Equal(pendientes, resultado.Paradas.Select(p => p.EnvioId));
        Assert.NotNull(resultado.RegresoEstimadoBaseEnUtc);
    }
}
