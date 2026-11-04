using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using NexoRuta.Application.Envios.Exceptions;
using NexoRuta.Infrastructure.Envios.Geocodificacion;

namespace NexoRuta.UnitTests.Envios;

public sealed class GeocodificadorGeoapifyTests
{
    [Fact]
    public async Task ClaveSolaNoAutorizaConsumoExterno()
    {
        var handler = new RespuestaHandler(HttpStatusCode.OK, """{"results":[]}""");
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { [GeocodificadorGeoapify.ClaveConfiguracion] = "test-secret" }).Build();
        await Assert.ThrowsAsync<GeocodificacionNoDisponibleException>(() =>
            new GeocodificadorGeoapify(new Clientes(handler), config, cache).BuscarAsync("Calle de prueba", default));
        Assert.Empty(handler.Pedidos);
    }
    [Fact]
    public async Task MantieneAmbiguedadCodificaConsultaYReutilizaCacheSinElegirPunto()
    {
        var handler = new RespuestaHandler(HttpStatusCode.OK, """{"results":[{"formatted":"Primera","lat":-34.9,"lon":-56.2},{"formatted":"Segunda","lat":-34.8,"lon":-56.1}]}""");
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var geo = Crear(handler, cache);
        var propuestas = await geo.BuscarAsync("  Av. de prueba 123 & esquina 456  ", default);
        Assert.Equal(2, propuestas.Count);
        Assert.Equal(propuestas, await geo.BuscarAsync("Av. de prueba 123 & esquina 456", default));
        Assert.Single(handler.Pedidos); Assert.Equal("api.geoapify.com", handler.Pedidos[0].Host);
        Assert.Contains("text=Av.%20de%20prueba%20123%20%26%20esquina%20456", handler.Pedidos[0].Query);
        Assert.Contains("lang=es&limit=5", handler.Pedidos[0].Query);
        Assert.Contains("Geoapify", geo.Atribucion);
    }
    [Theory]
    [InlineData(200, "{}")]
    [InlineData(200, "not-json")]
    [InlineData(200, "{\"results\":[{\"formatted\":\"Dato\",\"lat\":91,\"lon\":0}]}")]
    [InlineData(200, "{\"results\":[{\"formatted\":\"Dato\",\"lon\":0}]}")]
    [InlineData(503, "UPSTREAM_PRIVATE")]
    public async Task FallosNoProducenPuntosFicticiosNiRevelanErrorOClave(int status, string cuerpo)
    {
        var handler = new RespuestaHandler((HttpStatusCode)status, cuerpo); using var cache = new MemoryCache(new MemoryCacheOptions());
        var error = await Assert.ThrowsAsync<GeocodificacionNoDisponibleException>(() => Crear(handler, cache).BuscarAsync("Dirección de prueba", default));
        Assert.Null(error.InnerException); Assert.DoesNotContain("PRIVATE", error.ToString()); Assert.DoesNotContain("test-secret", error.ToString());
    }
    [Fact]
    public async Task SinCoincidenciasEsDistintoDeProveedorNoConfigurado()
    {
        var handler = new RespuestaHandler(HttpStatusCode.OK, """{"results":[]}"""); using var cache = new MemoryCache(new MemoryCacheOptions());
        Assert.Empty(await Crear(handler, cache).BuscarAsync("Dirección de prueba", default));
        var config = new ConfigurationBuilder().Build();
        var noConfigurado = new GeocodificadorGeoapify(new Clientes(handler), config, cache);
        await Assert.ThrowsAsync<GeocodificacionNoDisponibleException>(() => noConfigurado.BuscarAsync("Otra dirección", default));
        Assert.Single(handler.Pedidos);
    }
    [Fact]
    public async Task LimitaRespuestaExternaYPropagaCancelacionDelSolicitante()
    {
        var handler = new RespuestaHandler(HttpStatusCode.OK, new string('x', 70_000)); using var cache = new MemoryCache(new MemoryCacheOptions());
        await Assert.ThrowsAsync<GeocodificacionNoDisponibleException>(() => Crear(handler, cache).BuscarAsync("Dirección de prueba", default));
        using var ct = new CancellationTokenSource(); ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Crear(handler, cache).BuscarAsync("Otra dirección", ct.Token));
    }
    private static GeocodificadorGeoapify Crear(HttpMessageHandler handler, IMemoryCache cache)
        => new(new Clientes(handler), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            { [GeocodificadorGeoapify.ClaveConfiguracion] = "test-secret", ["Geocoding:Geoapify:PermitirLlamadasReales"] = "true" }).Build(), cache, new ConsumoPrueba());
    private sealed class ConsumoPrueba : IConsumoGeocodificacion
    { public Task ReservarAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; } }

    [Fact]
    public async Task AutocompletadoUsaGeoapifySinConsultarTextosCortosYSeparaCacheDeBusquedaCompleta()
    {
        var handler = new RespuestaHandler(HttpStatusCode.OK, """{"results":[{"formatted":"Calle sintética 123","lat":-34.9,"lon":-56.2}]}""");
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var geo = Crear(handler, cache);
        Assert.Empty(await geo.BuscarAsync("ab", default, true));
        Assert.Empty(handler.Pedidos);
        var result = await geo.BuscarAsync("Calle & 123", default, true);
        Assert.Equal(-34.9, Assert.Single(result).Latitud);
        Assert.Equal("/v1/geocode/autocomplete", Assert.Single(handler.Pedidos).AbsolutePath);
        await geo.BuscarAsync("Calle & 123", default, true);
        Assert.Single(handler.Pedidos);
        await geo.BuscarAsync("Calle & 123", default);
        Assert.Equal("/v1/geocode/search", handler.Pedidos[1].AbsolutePath);
        Assert.All(handler.Pedidos, u => Assert.Equal("api.geoapify.com", u.Host));
    }

    [Theory]
    [InlineData(402)]
    [InlineData(429)]
    public async Task CuotaDelProveedorCierraCircuitoSinReintentarNiExponerElCuerpo(int status)
    {
        var handler = new RespuestaHandler((HttpStatusCode)status, "UPSTREAM_PRIVATE");
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var geo = Crear(handler, cache);
        for (var i = 0; i < 2; i++)
        {
            var error = await Assert.ThrowsAsync<GeocodificacionLimiteException>(() => geo.BuscarAsync("Calle " + i, default, true));
            Assert.True(error.CuotaAgotada);
            Assert.DoesNotContain("PRIVATE", error.ToString());
        }
        Assert.Single(handler.Pedidos);
    }

    [Fact]
    public async Task CuotaLocalNoConsultaProveedor()
    {
        var handler = new RespuestaHandler(HttpStatusCode.OK, """{"results":[]}""");
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { [GeocodificadorGeoapify.ClaveConfiguracion] = "test-secret", ["Geocoding:Geoapify:PermitirLlamadasReales"] = "true" }).Build();
        var geo = new GeocodificadorGeoapify(new Clientes(handler), config, cache, new SinCupo());
        await Assert.ThrowsAsync<GeocodificacionLimiteException>(() => geo.BuscarAsync("Calle de prueba", default, true));
        Assert.Empty(handler.Pedidos);
    }
    private sealed class SinCupo : IConsumoGeocodificacion
    { public Task ReservarAsync(CancellationToken ct) => throw new GeocodificacionLimiteException(true); }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TimeoutOFalloDeRedSeCierraSinReintentarNiFiltrarDetalle(bool timeout)
    {
        var handler = new FalloTransporte(timeout);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [GeocodificadorGeoapify.ClaveConfiguracion] = "test-secret",
            ["Geocoding:Geoapify:PermitirLlamadasReales"] = "true",
            ["Geocoding:Consumo:TimeoutSegundos"] = "1"
        }).Build();
        var geo = new GeocodificadorGeoapify(new Clientes(handler), config, cache, new ConsumoPrueba());
        var error = await Assert.ThrowsAsync<GeocodificacionNoDisponibleException>(() => geo.BuscarAsync("Calle sintética", default, true));
        Assert.Equal(1, handler.Calls); Assert.Null(error.InnerException);
        Assert.DoesNotContain("PRIVATE", error.ToString());
    }
    private sealed class FalloTransporte(bool timeout) : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (timeout) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new HttpRequestException("UPSTREAM_PRIVATE");
        }
    }
    private sealed class Clientes(HttpMessageHandler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, disposeHandler: false); }
    private sealed class RespuestaHandler(HttpStatusCode status, string cuerpo) : HttpMessageHandler
    {
        public List<Uri> Pedidos { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Pedidos.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(cuerpo) });
        }
    }
}
