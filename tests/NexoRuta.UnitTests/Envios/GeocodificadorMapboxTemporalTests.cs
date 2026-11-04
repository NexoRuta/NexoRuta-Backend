using System.Net;
using Microsoft.Extensions.Configuration;
using NexoRuta.Application.Envios.Exceptions;
using NexoRuta.Infrastructure.Envios.Geocodificacion;
namespace NexoRuta.UnitTests.Envios;

public sealed class GeocodificadorMapboxTemporalTests
{
    [Fact] public async Task OnlyTemporaryForwardRequestsWithoutCacheRetryBatchOrPermanentFallback()
    {
        var handler = new Transport(); var budget = new Budget(); var geo = Create(handler,budget);
        Assert.Empty(await geo.BuscarAsync("ab",default,true)); Assert.Equal(0,budget.Count);
        for (var i=0;i<2;i++) Assert.Single(await geo.BuscarAsync("Calle & 123",default,true));
        Assert.Equal(2,budget.Count); Assert.Equal(2,handler.Count);
        handler.Status=429;
        await Assert.ThrowsAsync<GeocodificacionLimiteException>(()=>geo.BuscarAsync("Otra calle",default));
        Assert.Equal(3,handler.Count); Assert.Equal(3,budget.Count);
    }
    [Fact] public async Task FlagAndPublicTokenRequiredBeforeReservationOrHttp()
    {
        var handler=new Transport();var budget=new Budget();
        await Assert.ThrowsAsync<GeocodificacionNoDisponibleException>(()=>Create(handler,budget,false).BuscarAsync("Calle",default));
        Assert.Equal(0,budget.Count);Assert.Equal(0,handler.Count);
    }
    [Fact] public async Task CancellationAfterReservationIsNotRefundedAndNeverRetried()
    {
        var handler=new Transport { Cancel=true };var budget=new Budget();var geo=Create(handler,budget);
        using var ct=new CancellationTokenSource();handler.Caller=ct;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>geo.BuscarAsync("Calle",ct.Token));
        Assert.Equal(1,budget.Count);Assert.Equal(1,handler.Count);
    }
    [Theory] [InlineData("{}")] [InlineData("not-json")]
    [InlineData("{\"features\":[{\"geometry\":{\"coordinates\":[0,91]},\"properties\":{\"name\":\"Invalid\"}}]}")]
    public async Task MissingOrInvalidDataIsControlledWithoutZeroCoordinatesOrSecrets(string body)
    {
        var handler=new Transport{Body=body};
        var error=await Assert.ThrowsAsync<GeocodificacionNoDisponibleException>(()=>Create(handler,new Budget()).BuscarAsync("Calle",default));
        Assert.Null(error.InnerException);Assert.DoesNotContain("synthetic",error.ToString());Assert.Equal(1,handler.Count);
    }
    private static GeocodificadorMapboxTemporal Create(Transport handler,Budget budget,bool enabled=true)=>new(new Clients(handler),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{
            ["Geocoding:MapboxTemporal:Habilitado"]=enabled.ToString(),["Mapbox:PublicAccessToken"]="pk.synthetic"}).Build(),budget);
    private sealed class Budget:IConsumoGeocodificacion {public int Count;public Task ReservarAsync(CancellationToken ct){ct.ThrowIfCancellationRequested();Count++;return Task.CompletedTask;}}
    private sealed class Clients(Transport h):IHttpClientFactory {public HttpClient CreateClient(string name)=>new(h,false);}
    private sealed class Transport:HttpMessageHandler
    {
        public int Count,Status=200;public bool Cancel;public CancellationTokenSource? Caller;
        public string Body="""{"features":[{"geometry":{"coordinates":[-56.2,-34.9]},"properties":{"full_address":"Calle 123"}}]}""";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
        {
            Count++;Assert.Equal(HttpMethod.Get,r.Method);Assert.Equal("api.mapbox.com",r.RequestUri!.Host);
            Assert.Equal("/search/geocode/v6/forward",r.RequestUri.AbsolutePath);
            var query=System.Web.HttpUtility.ParseQueryString(r.RequestUri.Query);
            Assert.Equal(new[]{"false"},query.GetValues("permanent"));
            Assert.Contains("permanent=false",r.RequestUri.Query);Assert.DoesNotContain("permanent=true",r.RequestUri.Query);
            Assert.DoesNotContain("places-permanent",r.RequestUri.ToString());Assert.DoesNotContain("batch",r.RequestUri.ToString());
            if(Cancel){Caller!.Cancel();ct.ThrowIfCancellationRequested();}
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)Status){Content=new StringContent(Body)});
        }
    }
}
