using System.Net;
using static NexoRuta.IntegrationTests.Fixtures.RepartoHttpData;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.Domain.Envios;
using NexoRuta.IntegrationTests.Fixtures;

namespace NexoRuta.IntegrationTests.Reparto;

public sealed class RutasHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task BorradorVacioReservaRecursosPeroNoSeDespachaNiSeDescarga()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var p = await Preparar(data, client);
        var recursos = (await client.GetFromJsonAsync<RecursosPlanificacionResponse>("/api/rutas/recursos"))!;
        Assert.Contains(recursos.Repartidores, r => r.AccesoId == p.AccesoId);
        Assert.Contains(recursos.Vehiculos, v => v.Id == p.VehiculoId);
        using var incompleta = await client.PostAsJsonAsync("/api/rutas", new { RepartidorAccesoId = p.AccesoId,
            p.VehiculoId, Fecha = FechaOperacion });
        Assert.Equal(HttpStatusCode.BadRequest, incompleta.StatusCode);
        using var plan = await client.PostAsJsonAsync("/api/rutas", new { RepartidorAccesoId = p.AccesoId,
            p.VehiculoId, Fecha = FechaOperacion, Paradas = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.OK, plan.StatusCode);
        var hoja = (await plan.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
        Assert.Empty(hoja.Paradas);
        using var despacho = await client.PostAsJsonAsync($"/api/rutas/{hoja.Id}/despacho", new { hoja.Version });
        Assert.Equal(HttpStatusCode.Conflict, despacho.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<HojaRutaResponse[]>($"/api/mobile/rutas?fecha={FechaOperacion:yyyy-MM-dd}"))!);
        await using var db = data.CreateDbContext();
        Assert.Equal(EstadoEnvio.EnDeposito, (await db.Envios.SingleAsync(e => e.Id == p.EnvioId)).Estado);
        await client.SetBearerForAccessAsync(data, (await db.AccesosUsuario.SingleAsync(a => a.ComercioId != null)).Id);
        using var comercio = await client.GetAsync("/api/rutas/recursos");
        Assert.Equal(HttpStatusCode.Forbidden, comercio.StatusCode);
    }
    [Fact]
    public async Task RecibirPlanificarDespacharYDescargarConservaVersionPoliticaYDestino()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var preparado = await Preparar(data, client);
        using var plan = await client.PostAsJsonAsync("/api/rutas", Solicitud(preparado));
        Assert.Equal(HttpStatusCode.OK, plan.StatusCode);
        var hoja = (await plan.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
        Assert.Equal("Planificada", hoja.Estado);
        Assert.Single(hoja.Paradas);
        Assert.Equal((-34.9, -56.2), (hoja.Paradas[0].Latitud, hoja.Paradas[0].Longitud));
        Assert.Empty((await client.GetFromJsonAsync<HojaRutaResponse[]>($"/api/mobile/rutas?fecha={FechaOperacion:yyyy-MM-dd}"))!);
        using var despacho = await client.PostAsJsonAsync($"/api/rutas/{hoja.Id}/despacho", new { hoja.Version });
        Assert.Equal(HttpStatusCode.OK, despacho.StatusCode);
        var propia = Assert.Single((await client.GetFromJsonAsync<HojaRutaResponse[]>($"/api/mobile/rutas?fecha={FechaOperacion:yyyy-MM-dd}"))!);
        Assert.Equal("Despachada", propia.Estado);
        Assert.Equal(preparado.AccesoId, propia.RepartidorAccesoId);
        Assert.Equal(1, propia.Politica.Version);
        Assert.All(propia.Paradas[0].Bultos, x => Assert.Equal("Deposito", x.Custodia));
        using var antiguo = await client.PostAsJsonAsync($"/api/rutas/{hoja.Id}/despacho", new { hoja.Version });
        Assert.Equal(HttpStatusCode.Conflict, antiguo.StatusCode);
        await using var db = data.CreateDbContext();
        Assert.Equal(EstadoEnvio.AsignadoRuta, (await db.Envios.SingleAsync(x => x.Id == preparado.EnvioId)).Estado);
    }

    [Fact]
    public async Task ConfiguracionExplicitaYCapacidadSonObligatoriasSinAsignarSiFalla()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var preparado = await Preparar(data, client, politica: false);
        using var sinPolitica = await client.PostAsJsonAsync("/api/rutas", Solicitud(preparado));
        Assert.Equal(HttpStatusCode.Conflict, sinPolitica.StatusCode);
        using var config = await client.PutAsJsonAsync("/api/reparto/configuracion", Politica());
        Assert.Equal(HttpStatusCode.OK, config.StatusCode);
        await using var db = data.CreateDbContext();
        var vehiculo = await db.Vehiculos.SingleAsync(x => x.Id == preparado.VehiculoId);
        vehiculo.Actualizar(vehiculo.Matricula, 0.01m, 0.01m, true); await db.SaveChangesAsync();
        using var exceso = await client.PostAsJsonAsync("/api/rutas", Solicitud(preparado));
        Assert.Equal(HttpStatusCode.Conflict, exceso.StatusCode);
        Assert.Empty(await db.HojasRuta.ToArrayAsync());
        Assert.Equal(EstadoEnvio.EnDeposito, (await db.Envios.SingleAsync(x => x.Id == preparado.EnvioId)).Estado);
    }

    [Fact]
    public async Task DosPlanificacionesConcurrentesSoloAsignanUnaVez()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var preparado = await Preparar(data, client);
        var respuestas = await Task.WhenAll(client.PostAsJsonAsync("/api/rutas", Solicitud(preparado)),
            client.PostAsJsonAsync("/api/rutas", Solicitud(preparado)));
        Assert.Equal(1, respuestas.Count(x => x.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, respuestas.Count(x => x.StatusCode == HttpStatusCode.Conflict));
        foreach (var respuesta in respuestas) respuesta.Dispose();
        await using var db = data.CreateDbContext(); Assert.Single(await db.HojasRuta.ToArrayAsync());
    }

    [Fact]
    public async Task ComercioNoDespachaYCoordenadasNoSeCambianTrasRecepcion()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var preparado = await Preparar(data, client);
        await using var db = data.CreateDbContext();
        await client.SetBearerForAccessAsync(data, (await db.AccesosUsuario.SingleAsync(x => x.ComercioId != null)).Id);
        using var plan = await client.PostAsJsonAsync("/api/rutas", Solicitud(preparado));
        Assert.Equal(HttpStatusCode.Forbidden, plan.StatusCode);
        using var cambio = await client.PutAsJsonAsync($"/api/envios/{preparado.EnvioId}/destino", new { Version = 1, Latitud = -34.1, Longitud = -56.1 });
        Assert.Equal(HttpStatusCode.Conflict, cambio.StatusCode);
        using var propias = await client.GetAsync($"/api/mobile/rutas?fecha={FechaOperacion:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.Forbidden, propias.StatusCode);
    }

    [Fact]
    public async Task SinDestinoConfirmadoNoSePuedePlanificar()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var preparado = await Preparar(data, client, confirmar: false);
        using var plan = await client.PostAsJsonAsync("/api/rutas", Solicitud(preparado));
        Assert.Equal(HttpStatusCode.Conflict, plan.StatusCode);
    }

}
