using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NexoRuta.Application.Reparto.Estimaciones;
using NexoRuta.Infrastructure.Reparto;
namespace NexoRuta.UnitTests.Reparto;
public sealed class GeoapifyRoutingTests
{
    private static SolicitudTramoVial Pedido()=>new("LOCAL-HASH",0,new(-34.9,-56.2),new(-34.8,-56.1),DateTimeOffset.UtcNow,"drive");
    [Theory]
    [InlineData(false,false)]
    [InlineData(true,false)]
    [InlineData(false,true)]
    public async Task DisabledByDefaultAndGeocoderKeyIsNeverReused(bool enabled,bool dedicatedKey)
    {
        var handler=new Handler();var budget=new Budget();var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Geocoding:Geoapify:ApiKey","DO-NOT-REUSE"},{"RoutingGeoapify:ApiKey",dedicatedKey?"DEDICATED-TEST":null}}).Build();
        var r=await new GeoapifyRouting(new Factory(handler),config,new(){PermitirLlamadasReales=enabled},budget,TimeProvider.System).CalcularAsync(Pedido(),default);Assert.Equal(EstadoTramoVial.NoConfigurado,r.Estado);Assert.Equal(0,handler.Calls);Assert.Equal(0,budget.Calls);
    }
    [Theory]
    [InlineData(200,"{\"results\":[]}",EstadoTramoVial.RutaNoEncontrada)]
    [InlineData(200,"broken",EstadoTramoVial.RespuestaInvalida)]
    [InlineData(200,"{\"results\":[{\"legs\":[{\"distance\":10}]}]}",EstadoTramoVial.RespuestaInvalida)]
    [InlineData(429,"secret upstream text",EstadoTramoVial.CuotaExcedida)]
    [InlineData(503,"private upstream error",EstadoTramoVial.ProveedorNoDisponible)]
    [InlineData(401,"bad key",EstadoTramoVial.ProveedorNoDisponible)]
    public async Task ControlledHttpErrorsAreTypedAndNotRetried(int code,string body,EstadoTramoVial expected)
    {
        var h=new Handler{Code=code,Body=body};var r=await Provider(h,new()).CalcularAsync(Pedido(),default);Assert.Equal(expected,r.Estado);Assert.Equal(1,h.Calls);Assert.Null(r.DuracionSegundos);Assert.DoesNotContain("upstream",JsonSerializer.Serialize(r));
    }
    [Fact]
    public async Task SuccessfulResponseIsStaticAttributedAndHasNoInventedExpiry()
    {
        var h=new Handler{Body="{\"results\":[{\"distance_units\":\"Meters\",\"legs\":[{\"time\":60,\"distance\":100}]}]}"};var r=await Provider(h,new()).CalcularAsync(Pedido(),default);Assert.Equal(EstadoTramoVial.Encontrado,r.Estado);Assert.Equal(60,r.DuracionSegundos);Assert.Equal(ModeloTrafico.SinTrafico,r.Procedencia!.Trafico);Assert.Null(r.Procedencia.ValidoHastaUtc);Assert.Contains("OpenStreetMap",r.Procedencia.Atribucion);Assert.Contains("traffic=free_flow",h.Uri!.Query);Assert.DoesNotContain("DO-NOT-REUSE",h.Uri.Query);
    }
    [Fact]
    public async Task ExhaustedBudgetMakesNoHttpCallAndCallerCancellationPropagates()
    {
        var h=new Handler();var r=await Provider(h,new(){Available=false}).CalcularAsync(Pedido(),default);Assert.Equal(EstadoTramoVial.CuotaExcedida,r.Estado);Assert.Equal(0,h.Calls);using var cts=new CancellationTokenSource();cts.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Provider(h,new()).CalcularAsync(Pedido(),cts.Token));
    }
    [Fact]
    public async Task RequestTimeoutIsBoundedAndDoesNotRetry()
    {
        var h=new Handler{Wait=true};var r=await Provider(h,new()).CalcularAsync(Pedido(),default);Assert.Equal(EstadoTramoVial.TiempoAgotado,r.Estado);Assert.Equal(1,h.Calls);
    }
    private static GeoapifyRouting Provider(Handler h,Budget b)=>new(new Factory(h),new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"RoutingGeoapify:ApiKey","DEDICATED-TEST-KEY"}}).Build(),new(){PermitirLlamadasReales=true,TimeoutSegundos=1},b,TimeProvider.System);
    private sealed class Factory(Handler h):IHttpClientFactory{public HttpClient CreateClient(string n)=>new(h,false);}
    private sealed class Budget:IConsumoRouting{public bool Available=true;public int Calls;public Task<bool> EsperarAsync(CancellationToken ct){Calls++;return Task.FromResult(Available);}}
    private sealed class Handler:HttpMessageHandler
    {public int Code=200;public string Body="{}";public bool Wait;public int Calls;public Uri? Uri;protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct){Calls++;Uri=r.RequestUri;if(Wait)await Task.Delay(Timeout.Infinite,ct);return new((HttpStatusCode)Code){Content=new StringContent(Body)};}}
}
