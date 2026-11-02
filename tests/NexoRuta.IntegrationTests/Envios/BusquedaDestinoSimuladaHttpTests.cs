using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexoRuta.Api.Features.Envios.Controllers;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Envios.Dtos;
using NexoRuta.Application.Envios.Exceptions;
using NexoRuta.Application.Envios.Interfaces;
using NexoRuta.Application.Envios.Repositories;
using NexoRuta.Application.Envios.UseCases;
using NexoRuta.Infrastructure.Envios.Geocodificacion;

namespace NexoRuta.IntegrationTests.Envios;

// Real MVC/use case/adapter, synthetic repository and provider transport. No database or provider connections.
public sealed class BusquedaDestinoSimuladaHttpTests
{
    private static readonly Guid Shipment = Guid.NewGuid(), Commerce = Guid.NewGuid();

    [Fact]
    public async Task BuscaSinEscribirConfirmaYVuelveAEditarConElContratoExistente()
    {
        var provider = new Provider(); var repo = new Repository();
        await using var app = await Host(repo, provider);
        using var client = app.GetTestClient(); var url = $"/api/envios/{Shipment}/destino";
        using var result = await client.PostAsJsonAsync(url + "/busqueda", new { Consulta = "  Calle sintética  " });
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var proposals = (await result.Content.ReadFromJsonAsync<PropuestasDestinoDatos>())!;
        Assert.Equal("Registrada 123", proposals.Direccion);
        Assert.Contains("Mapbox", proposals.Atribucion);
        Assert.Null(repo.Current.Latitud);
        Assert.Equal("/search/geocode/v6/forward", Assert.Single(provider.Requests).AbsolutePath);
        for (var version = 0; version < 2; version++)
        {
            using var save = await client.PutAsJsonAsync(url, new { Version = version, Latitud = -34.9 + version * .01, Longitud = -56.2 });
            Assert.Equal(HttpStatusCode.NoContent, save.StatusCode);
            var reopened = (await client.GetFromJsonAsync<DestinoEnvioDatos>(url))!;
            Assert.Equal(version + 1, reopened.Version);
            Assert.Equal(-34.9 + version * .01, reopened.Latitud);
            Assert.Equal("Registrada 123", reopened.Direccion);
        }
        using var oldEndpoint = await client.PostAsync(url + "/propuestas", null);
        Assert.Equal(HttpStatusCode.OK, oldEndpoint.StatusCode);
        Assert.Equal("/search/geocode/v6/forward", provider.Requests[1].AbsolutePath);
        Assert.Equal(2, provider.Requests.Count); // Saving/reopening never geocodes.
    }

    [Theory]
    [InlineData(402, 429)]
    [InlineData(429, 429)]
    [InlineData(401, 503)]
    [InlineData(500, 503)]
    public async Task ErroresExternosNoRevelanClaveNiModificanDestino(int upstream, int expected)
    {
        var provider = new Provider { Status = upstream }; var repo = new Repository();
        await using var app = await Host(repo, provider); using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync($"/api/envios/{Shipment}/destino/busqueda", new { Consulta = "Calle" });
        Assert.Equal(expected, (int)response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("UPSTREAM_PRIVATE", body); Assert.DoesNotContain("pk.synthetic-key", body);
        Assert.Null(repo.Current.Latitud); Assert.Equal(0, repo.Current.Version);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task ValidacionPropiedadEstadoYConfiguracionImpidenLlamadas()
    {
        var provider = new Provider(); var repo = new Repository();
        await using var app = await Host(repo, provider, enabled: false); using var client = app.GetTestClient();
        var url = $"/api/envios/{Shipment}/destino/busqueda";
        foreach (var input in new object[] { new { Consulta = "ab" }, new { Consulta = "    " }, new { Consulta = new string('x', 241) }, new { Consulta = "Calle", ApiKey = "invalid-extra-field" } })
        {
            using var invalid = await client.PostAsJsonAsync(url, input); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        using var foreign = await client.PostAsJsonAsync($"/api/envios/{Guid.NewGuid()}/destino/busqueda", new { Consulta = "Calle" });
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using var disabled = await client.PostAsJsonAsync(url, new { Consulta = "Calle" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabled.StatusCode);
        repo.Current = repo.Current with { Estado = "EnDeposito", ConfirmadaEnUtc = DateTimeOffset.UtcNow };
        using var locked = await client.PostAsJsonAsync(url, new { Consulta = "Calle" });
        Assert.Equal(HttpStatusCode.Conflict, locked.StatusCode);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task CuotaLocalDevuelve429SinLlamarAlProveedor()
    {
        var provider = new Provider(); var repo = new Repository();
        await using var app = await Host(repo, provider, quota: true); using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync($"/api/envios/{Shipment}/destino/busqueda", new { Consulta = "Calle" });
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("86400", response.Headers.GetValues("Retry-After").Single());
        Assert.Contains("geocoding_quota_exhausted", await response.Content.ReadAsStringAsync());
        Assert.Empty(provider.Requests);
    }

    private static async Task<WebApplication> Host(Repository repo, Provider provider, bool enabled = true, bool quota = false)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            { ["Mapbox:PublicAccessToken"] = "pk.synthetic-key", ["Geocoding:MapboxTemporal:Habilitado"] = enabled.ToString() });
        builder.Services.AddControllers().AddApplicationPart(typeof(DestinosEnvioController).Assembly);
        builder.Services.AddAuthorization(o => { o.AddPolicy("Comercio", p => p.RequireClaim("tipo", "Comercio")); o.AddPolicy("envios.crear", p => p.RequireClaim("permiso", "envios.crear")); });
        builder.Services.AddRateLimiter(o => o.AddConcurrencyLimiter("geocodificacion", limit => { limit.PermitLimit = 10; limit.QueueLimit = 0; }));
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<IDestinosEnvioRepository>(repo);
        builder.Services.AddSingleton<IUsuarioActual, Actor>();
        builder.Services.AddSingleton<IConsumoGeocodificacion>(new Budget(quota));
        builder.Services.AddSingleton<IHttpClientFactory>(new ClientFactory(provider));
        builder.Services.AddScoped<IGeocodificador, GeocodificadorMapboxTemporal>();
        builder.Services.AddKeyedScoped<GestionarDestinoEnvioUseCase>(GeocodificadorMapboxTemporal.Servicio,(p,_)=>new(p.GetRequiredService<IUsuarioActual>(),p.GetRequiredService<IDestinosEnvioRepository>(),p.GetRequiredService<IGeocodificador>()));
        builder.Services.AddScoped<GestionarDestinoEnvioUseCase>();
        var app = builder.Build(); app.UseRouting();
        app.Use((context, next) => { context.User = new ClaimsPrincipal(new ClaimsIdentity([new("tipo", "Comercio"), new("permiso", "envios.crear")], "synthetic")); return next(); });
        app.UseAuthorization(); app.UseRateLimiter(); app.MapControllers(); await app.StartAsync(); return app;
    }
    private sealed class Actor : IUsuarioActual
    {
        public Task<ContextoUsuario> ObtenerAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ContextoUsuario(Guid.NewGuid(), Guid.NewGuid(), null, Commerce, "synthetic@example.test", null, "Synthetic", Guid.NewGuid(), "Comercio"));
    }
    private sealed class Repository : IDestinosEnvioRepository
    {
        public DestinoEnvioDatos Current = new(Shipment, "Synthetic", "Registrada 123", 0, "Admitido", null, null, null);
        public Task<DestinoEnvioDatos?> ObtenerAsync(Guid comercioId, Guid envioId, CancellationToken ct) => Task.FromResult(comercioId == Commerce && envioId == Shipment ? Current : null);
        public Task<bool> ConfirmarAsync(Guid comercioId, Guid usuarioId, Guid envioId, long version, double latitud, double longitud, CancellationToken ct, string? direccion = null)
        {
            Assert.Equal(Current.Version, version);
            Current = Current with { Direccion = direccion ?? Current.Direccion, Version = version + 1, Latitud = latitud, Longitud = longitud, ConfirmadaEnUtc = DateTimeOffset.UtcNow };
            return Task.FromResult(true);
        }
    }
    private sealed class Budget(bool exhausted) : IConsumoGeocodificacion
    { public Task ReservarAsync(CancellationToken ct) => exhausted ? throw new GeocodificacionLimiteException(true) : Task.CompletedTask; }
    private sealed class ClientFactory(Provider provider) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(provider, disposeHandler: false); }
    private sealed class Provider : HttpMessageHandler
    {
        public int Status = 200;
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("api.mapbox.com", request.RequestUri!.Host);
            Requests.Add(request.RequestUri);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)Status)
            { Content = new StringContent(Status == 200 ? """{"features":[{"geometry":{"coordinates":[-56.2,-34.9]},"properties":{"full_address":"Calle sintética 123"}}]}""" : "UPSTREAM_PRIVATE") });
        }
    }
}
