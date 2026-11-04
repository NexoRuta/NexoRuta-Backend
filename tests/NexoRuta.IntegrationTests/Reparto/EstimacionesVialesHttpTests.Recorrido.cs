using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.Infrastructure.Reparto;
using static NexoRuta.IntegrationTests.Fixtures.RepartoHttpData;
namespace NexoRuta.IntegrationTests.Reparto;
public sealed partial class EstimacionesVialesHttpTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public async Task RecorridoUsesReceivedGpsAndPreservesManualInitialPhysicalFactsAndMidnight(bool restricted)
    {
        await using var f=await RecorridoSetup(restricted);await f.Gps(f.Clock.Now.AddSeconds(-10));
        var before=await f.Physical();var command=await f.Command();var r=await f.Calculate(command);
        Assert.Equal("Calculada",r.Estado);Assert.Equal("DuranteRecorrido",r.TipoCalculo);Assert.NotNull(r.PosicionFuente);
        Assert.Equal(f.Clock.Now.AddSeconds(-10),r.PosicionFuente.MedidaEnUtc);Assert.Equal(2,r.Tramos.Count);Assert.Single(r.Paradas);
        Assert.Equal(f.Clock.Now.AddMinutes(30),r.RegresoEstimadoBaseEnUtc);Assert.Equal(f.Clock.Now.AddMinutes(37),r.ListoPrevistoEnUtc);
        Assert.Equal(FechaOperacion.AddDays(1),DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(r.RegresoEstimadoBaseEnUtc!.Value,TimeZoneInfo.FindSystemTimeZoneById("America/Montevideo")).DateTime));
        var calls=f.Handler.Calls;Assert.Equal(r.NumeroVersion,(await f.Calculate(command)).NumeroVersion);Assert.Equal(calls,f.Handler.Calls);
        Assert.Equal(before,await f.Physical());var history=await f.History();Assert.Contains(history,x=>x.TipoCalculo=="Inicial"&&x.NumeroVersion==1);
        // A later receipt containing an older measurement advances the diary but must not rewind this projection.
        await f.Gps(f.Clock.Now.AddSeconds(-30));history=await f.History();Assert.Equal(r.PosicionFuente.Id,history.First(x=>x.TipoCalculo=="DuranteRecorrido").PosicionFuente!.Id);
        Assert.Equal("Desconocida",history.First(x=>x.TipoCalculo=="DuranteRecorrido").Vigencia);
        f.Handler.Status=HttpStatusCode.TooManyRequests;var failed=await f.Calculate(await f.Command());Assert.Equal("CuotaExcedida",failed.Estado);
        Assert.Contains(await f.History(),x=>x.NumeroVersion==r.NumeroVersion&&x.Estado=="Calculada"&&x.CalculadoEnUtc==r.CalculadoEnUtc);
    }
    [Theory][InlineData(121,20,"GpsAntiguo")][InlineData(10,101,"GpsImpreciso")][InlineData(-1,20,"GpsFuturo")][InlineData(0,0,"SinPosicion")]
    public async Task RecorridoRejectsMissingOldImpreciseOrFutureGpsBeforeHttp(int age,double precision,string cause)
    {
        await using var f=await RecorridoSetup();if(precision>0)await f.Gps(f.Clock.Now.AddSeconds(-age),precision);
        var calls=f.Handler.Calls;var before=await f.Physical();var r=await f.Calculate(await f.Command());Assert.Equal("PosicionNoAdmisible",r.Estado);Assert.Equal(cause,r.Motivo);Assert.Empty(r.Tramos);Assert.Equal(calls,f.Handler.Calls);Assert.Equal(before,await f.Physical());
    }
    [Fact]public async Task RecorridoOnlyReturnsToBaseAfterRenditionAndNeverReceivesOrReleases()
    {
        await using var f=await RecorridoSetup();await f.Gps(f.Clock.Now.AddSeconds(-1));
        await using(var db=f.Data.CreateDbContext()){var b=await db.Bultos.SingleAsync();await f.Op("Rendicion",new{BultoIds=new[]{b.Id}});}
        var before=await f.Physical();var r=await f.Calculate(await f.Command());Assert.Equal("Calculada",r.Estado);Assert.Empty(r.Paradas);Assert.Single(r.Tramos);Assert.Equal(f.Clock.Now.AddMinutes(10),r.RegresoEstimadoBaseEnUtc);Assert.Equal(f.Clock.Now.AddMinutes(17),r.ListoPrevistoEnUtc);Assert.Equal(before,await f.Physical());
    }
    [Fact]public async Task RecorridoConcurrentNewerResponseWinsAndSameCommandHasOneRecord()
    {
        await using var f=await RecorridoSetup();await f.Gps(f.Clock.Now.AddSeconds(-1));
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int arrivals=0;
        f.Handler.Before=async ct=>{if(Interlocked.Increment(ref arrivals)==1){entered.SetResult();await release.Task.WaitAsync(ct);}};
        var command=await f.Command();var old=f.Client.PostAsJsonAsync($"/api/rutas/{f.Route.Id}/estimaciones/recorrido",command);await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var newer=await f.Calculate(await f.Command());release.SetResult();using var stale=await old;Assert.Equal(HttpStatusCode.Conflict,stale.StatusCode);
        var again=await f.Command();f.Handler.Before=null;var pair=await Task.WhenAll(f.Calculate(again),f.Calculate(again));Assert.Equal(pair[0].NumeroVersion,pair[1].NumeroVersion);
        var history=await f.History();Assert.Single(history,x=>x.ComandoId==again.ComandoId);Assert.Equal("Invalidada",history.Single(x=>x.NumeroVersion==newer.NumeroVersion).Vigencia);
    }
    [Theory][InlineData("retorno")][InlineData("liberacion")][InlineData("permiso")][InlineData("perfil")][InlineData("gps")][InlineData("pendientes")]
    public async Task RecorridoRevalidatesRelevantFactsAfterIo(string change)
    {
        await using var f=await RecorridoSetup();await f.Gps(f.Clock.Now.AddSeconds(-1));if(change is "retorno" or "liberacion")await f.Calculate(await f.Command());var command=await f.Command();var historyBefore=(await f.History()).Length;bool once=false;
        f.Handler.Before=async ct=>{
            if(once)return;once=true;
            if(change is "retorno" or "liberacion"){
                await f.Op("FinJornada",new{});var fleet=(await f.Client.GetFromJsonAsync<VehiculoFlotaResponse[]>("/api/reparto/flota"))!.Single();var o=fleet.Ocupaciones.Single();
                using var resp=await f.Client.PostAsJsonAsync($"/api/reparto/flota/{f.Prepared.VehiculoId}/retorno",new{ComandoId=Guid.NewGuid(),OcupacionId=o.Id,Version=o.Version,Motivo="Constatación sintética aislada"},ct);Assert.Equal(HttpStatusCode.OK,resp.StatusCode);
                if(change=="liberacion"){
                    var closed=(await f.Client.GetFromJsonAsync<HojaRutaResponse>($"/api/mobile/rutas/{f.Route.Id}"))!;
                    using var receive=await f.Client.PostAsJsonAsync($"/api/deposito/envios/{f.Prepared.EnvioId}/recepcion",new{Version=closed.Paradas.Single().EnvioVersion});Assert.Equal(HttpStatusCode.NoContent,receive.StatusCode);
                    using var free=await f.Client.PostAsJsonAsync($"/api/reparto/flota/{f.Prepared.VehiculoId}/liberar",new{ComandoId=Guid.NewGuid(),OcupacionId=o.Id,Version=o.Version+1,Motivo="Conciliación sintética"});Assert.Equal(HttpStatusCode.OK,free.StatusCode);
                }
            }else if(change=="permiso"){
                await using var db=f.Data.CreateDbContext();var access=await db.AccesosUsuario.SingleAsync(x=>x.Id==f.Prepared.AccesoId);access.RetirarRol();await db.SaveChangesAsync(ct);
            }else if(change=="perfil"){
                using var resp=await f.Client.PutAsJsonAsync($"/api/reparto/perfiles-viales/{f.Prepared.VehiculoId}",new{Version=1,Compatibilidad="NoCompatible",ConfirmarCompatibilidad=false,Motivo="Cambio controlado"},ct);Assert.Equal(HttpStatusCode.OK,resp.StatusCode);
            }else if(change=="gps"){await f.Gps(f.Clock.Now);}
            else {await using var db=f.Data.CreateDbContext();var b=await db.Bultos.SingleAsync();await f.Op("Rendicion",new{BultoIds=new[]{b.Id}});}
        };
        using var result=await f.Client.PostAsJsonAsync($"/api/rutas/{f.Route.Id}/estimaciones/recorrido",command);Assert.Equal(change=="permiso"?HttpStatusCode.Forbidden:HttpStatusCode.Conflict,result.StatusCode);
        await using var verify=f.Data.CreateDbContext();Assert.Equal(historyBefore,await verify.Set<EstimacionRegistro>().CountAsync());
        if(change is "retorno" or "liberacion"){
            var history=await f.History();Assert.DoesNotContain(history,x=>x.TipoCalculo=="DuranteRecorrido"&&x.Vigencia!="Invalidada");
            if(change=="retorno"){var noProjection=await f.Calculate(await f.Command());Assert.Equal("EjecucionNoDisponible",noProjection.Estado);}
            else{using var noProjection=await f.Client.PostAsJsonAsync($"/api/rutas/{f.Route.Id}/estimaciones/recorrido",command with{ComandoId=Guid.NewGuid()});Assert.Equal(HttpStatusCode.Conflict,noProjection.StatusCode);}
        }
    }
    [Fact]public async Task RecorridoCannotReadAnotherTenantOrUseAnotherOccupationAndDefaultMakesNoHttp()
    {
        await using var f=await RecorridoSetup(enabled:false);await f.Gps(f.Clock.Now.AddSeconds(-1));var before=await f.Physical();var r=await f.Calculate(await f.Command());Assert.Equal("ProveedorNoConfigurado",r.Estado);Assert.Equal(0,f.Handler.Calls);
        var cmd=await f.Command();using var wrong=await f.Client.PostAsJsonAsync($"/api/rutas/{f.Route.Id}/estimaciones/recorrido",cmd with{OcupacionId=Guid.NewGuid()});Assert.Equal(HttpStatusCode.Conflict,wrong.StatusCode);
        using var other=await f.Client.PostAsJsonAsync($"/api/rutas/{Guid.NewGuid()}/estimaciones/recorrido",cmd);Assert.Equal(HttpStatusCode.NotFound,other.StatusCode);Assert.Equal(before,await f.Physical());
    }
    [Fact]public async Task RecorridoRetainsLastSuccessBeyondFirstHistoryPageAndThresholdsAreSingleConfig()
    {
        await using var f=await RecorridoSetup();await f.Gps(f.Clock.Now.AddSeconds(-1));var success=await f.Calculate(await f.Command());
        f.Handler.Status=HttpStatusCode.TooManyRequests;
        for(int i=0;i<21;i++)Assert.Equal("CuotaExcedida",(await f.Calculate(await f.Command())).Estado);
        var history=await f.History();Assert.Contains(history,x=>x.NumeroVersion==success.NumeroVersion&&x.EsReferencia);Assert.Contains(history,x=>x.TipoCalculo=="Inicial"&&x.EsReferencia);
        Assert.Equal(20,history.Count(x=>!x.EsReferencia));Assert.Equal(120,history.First().CriteriosGps!.AntiguedadMaximaSegundos);Assert.Equal(100,history.First().CriteriosGps!.PrecisionMaximaMetros);
        var next=(await f.Client.GetFromJsonAsync<EstimacionRutaResponse[]>($"/api/rutas/{f.Route.Id}/estimaciones?antesDeVersion={history.Where(x=>!x.EsReferencia).Last().NumeroVersion}"))!;Assert.Equal(3,next.Length);Assert.All(next,x=>Assert.False(x.EsReferencia));
    }
    [Theory][InlineData(HttpStatusCode.InternalServerError,"ProveedorNoDisponible")][InlineData(HttpStatusCode.NotFound,"RespuestaInvalida")]
    public async Task RecorridoProviderFailurePublishesNoPartialEstimate(HttpStatusCode status,string state)
    {
        await using var f=await RecorridoSetup();await f.Gps(f.Clock.Now.AddSeconds(-1));f.Handler.Status=status;var before=await f.Physical();var result=await f.Calculate(await f.Command());Assert.Equal(state,result.Estado);Assert.Empty(result.Paradas);Assert.Empty(result.Tramos);Assert.Null(result.RegresoEstimadoBaseEnUtc);Assert.Equal(before,await f.Physical());
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task RecorridoTimeoutOrCancellationDoesNotPublishPartialOrPhysicalMutations(bool cancelled)
    {
        await using var f=await RecorridoSetup(timeout:1);await f.Gps(f.Clock.Now.AddSeconds(-1));var before=await f.Physical();var command=await f.Command();
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Handler.Before=async ct=>{entered.TrySetResult();await Task.Delay(Timeout.InfiniteTimeSpan,ct);};
        if(cancelled){using var cts=new CancellationTokenSource();var task=f.Client.PostAsJsonAsync($"/api/rutas/{f.Route.Id}/estimaciones/recorrido",command,cts.Token);await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));cts.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await task);await using var db=f.Data.CreateDbContext();Assert.Equal(1,await db.Set<EstimacionRegistro>().CountAsync());}
        else{var result=await f.Calculate(command);Assert.Equal("TiempoAgotado",result.Estado);Assert.Empty(result.Tramos);Assert.Null(result.RegresoEstimadoBaseEnUtc);}
        Assert.Equal(before,await f.Physical());
    }
    [Theory][InlineData("EntregaFisica")][InlineData("IntentoFallido")]
    public async Task RecorridoExcludesResolvedAndFailedVisitsEvenWhenFailedStopRemainsActive(string action)
    {
        await using var f=await RecorridoSetup();await f.Gps(f.Clock.Now.AddSeconds(-1));
        var current=(await f.Client.GetFromJsonAsync<HojaRutaResponse>($"/api/mobile/rutas/{f.Route.Id}"))!;
        string? motive=null;if(action=="IntentoFallido"){await using var db=f.Data.CreateDbContext();motive=await db.VersionesReglasOperativas.SelectMany(v=>v.Motivos).Where(m=>m.Disponible).Select(m=>m.Codigo).FirstAsync();}
        await f.Op(action,new{Nombre="Fixture",Motivo=motive},f.Prepared.EnvioId,current.Paradas.Single().EnvioVersion);
        var before=await f.Physical();var result=await f.Calculate(await f.Command());Assert.Equal("Calculada",result.Estado);Assert.Empty(result.Paradas);Assert.Single(result.Tramos);Assert.Equal(before,await f.Physical());
        if(action=="IntentoFallido"){await using var db=f.Data.CreateDbContext();Assert.True((await db.HojasRuta.Include(x=>x.Paradas).SingleAsync()).Paradas.Single().Activa);}
    }
    [Fact]public async Task RecorridoRlsHidesActualGpsOccupationAndHistoryFromOtherOperator()
    {
        await using var f=await RecorridoSetup(restricted:true);await f.Gps(f.Clock.Now.AddSeconds(-1));await f.Calculate(await f.Command());
        var runtime=await NexoRuta.IntegrationTests.Seguridad.RlsRestrictedHttpFlowTests.RestrictApiAsync(f.Data);
        await using var conn=new Npgsql.NpgsqlConnection(runtime.ConnectionString);await conn.OpenAsync();await using var tx=await conn.BeginTransactionAsync();
        await using(var ctx=new Npgsql.NpgsqlCommand("SELECT set_config('nexoruta.actor_kind','operador',true),set_config('nexoruta.org_id',@op,true)",conn,tx)){ctx.Parameters.AddWithValue("op",Guid.NewGuid().ToString());await ctx.ExecuteNonQueryAsync();}
        foreach(var table in new[]{"OperacionesJornada","OcupacionesRuta","EstimacionesRuta"}){await using var count=new Npgsql.NpgsqlCommand($"SELECT count(*) FROM \"{table}\"",conn,tx);Assert.Equal(0L,await count.ExecuteScalarAsync());}
    }
    [Fact]public async Task RecorridoDoesNotAdmitGpsFromOtherRouteOrCourierAndHandlesMalformedHistoricalData()
    {
        await using var f=await RecorridoSetup();using var planned=await f.Client.PostAsJsonAsync("/api/rutas",new{RepartidorAccesoId=f.Prepared.AccesoId,f.Prepared.VehiculoId,Fecha=FechaOperacion.AddDays(1),Paradas=Array.Empty<object>()});Assert.Equal(HttpStatusCode.OK,planned.StatusCode);var otherRoute=(await planned.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
        await using(var db=f.Data.CreateDbContext()){
            var a=await db.AccesosUsuario.SingleAsync(x=>x.Id==f.Prepared.AccesoId);var user=new NexoRuta.Domain.Administracion.Usuario($"gps-{Guid.NewGuid():N}@example.test","fixture-hash");db.Add(user);await db.SaveChangesAsync();
            var role=await db.Roles.SingleAsync(x=>x.Id==a.RolId);var other=new NexoRuta.Domain.Administracion.AccesoUsuario(user.Id,a.OperadorId,null,role);db.Add(other);await db.SaveChangesAsync();db.Add(new NexoRuta.Domain.Reparto.Repartidor(other.Id,a.OperadorId!.Value,"Other fixture courier"));await db.SaveChangesAsync();
            OperacionJornada Point(Guid route,Guid access,int seq)=>new(){Id=Guid.NewGuid(),OperadorId=a.OperadorId.Value,AccesoId=access,RutaId=route,Secuencia=seq,Tipo="Posicion",Resultado="Aplicada",Hash="fixture",Respuesta="{}",RecibidaEnUtc=f.Clock.GetUtcNow(),Payload=JsonSerializer.Serialize(new{datos=new{latitud=-34.8,longitud=-56.1,precisionMetros=5,posicionEnUtc=f.Clock.Now}})};
            // Synthetic historical rows deliberately test independent scope checks, not authorized mobile writes.
            db.Add(Point(otherRoute.Id,a.Id,1));db.Add(Point(f.Route.Id,other.Id,900));await db.SaveChangesAsync();
        }
        var calls=f.Handler.Calls;Assert.Equal("SinPosicion",(await f.Calculate(await f.Command())).Motivo);Assert.Equal(calls,f.Handler.Calls);
        await using(var db=f.Data.CreateDbContext()){
            var a=await db.AccesosUsuario.SingleAsync(x=>x.Id==f.Prepared.AccesoId);
            db.Add(new OperacionJornada{Id=Guid.NewGuid(),OperadorId=a.OperadorId!.Value,AccesoId=a.Id,RutaId=f.Route.Id,Secuencia=901,Tipo="Posicion",Resultado="Aplicada",Hash="fixture",Respuesta="{}",Payload="{}",RecibidaEnUtc=f.Clock.GetUtcNow()});await db.SaveChangesAsync();
        }
        Assert.Equal("GpsInvalido",(await f.Calculate(await f.Command())).Motivo);Assert.Equal(calls,f.Handler.Calls);
    }
    [Fact]public async Task RecorridoDoesNotRewindAfterAdmissionPrecisionIsTightened()
    {
        await using var f=await RecorridoSetup();await f.Gps(f.Clock.Now.AddSeconds(-20),10);await f.Gps(f.Clock.Now.AddSeconds(-10),80);
        var used=await f.Calculate(await f.Command());Assert.Equal(80,used.PosicionFuente!.PrecisionMetros);
        using var strict=f.Host.WithWebHostBuilder(b=>b.ConfigureTestServices(s=>s.AddSingleton(new GeoapifyRoutingOptions{PermitirLlamadasReales=true,Gps=new(){PrecisionMaximaMetros=50}})));
        using var client=strict.CreateClient();client.DefaultRequestHeaders.Authorization=f.Client.DefaultRequestHeaders.Authorization;
        var count=f.Handler.Calls;using var response=await client.PostAsJsonAsync($"/api/rutas/{f.Route.Id}/estimaciones/recorrido",await f.Command());Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var result=(await response.Content.ReadFromJsonAsync<EstimacionRutaResponse>())!;Assert.Equal("PosicionNoAdmisible",result.Estado);Assert.Null(result.PosicionFuente);Assert.Equal(count,f.Handler.Calls);
        var history=(await client.GetFromJsonAsync<EstimacionRutaResponse[]>($"/api/rutas/{f.Route.Id}/estimaciones"))!;Assert.Equal("GpsImpreciso",history.Single(x=>x.NumeroVersion==used.NumeroVersion).MotivoInvalidacion);
    }
    private async Task<RecorridoFixture> RecorridoSetup(bool restricted=false,bool enabled=true,int timeout=8)
    {
        var data=await postgres.CreateDatabaseAsync();using(var seed=data.CreateApiFactory())using(seed.CreateHttpClient()){}
        var runtime=restricted?await NexoRuta.IntegrationTests.Seguridad.RlsRestrictedHttpFlowTests.RestrictApiAsync(data):data;
        var settings=NexoRuta.IntegrationTests.Seguridad.RlsRestrictedHttpFlowTests.ApiSettings();settings["RoutingGeoapify:PermitirLlamadasReales"]=enabled?"true":"false";settings["RoutingGeoapify:ApiKey"]="SYNTHETIC-NOT-CREDENTIAL";settings["RoutingGeoapify:TimeoutSegundos"]=timeout.ToString();
        var clock=new RecorridoClock(DateTimeOffset.UtcNow);
        var handler=new RoadHandler();var factory=new IntegrationApiFactory(runtime,settings:settings,timeProvider:clock);var host=factory.WithWebHostBuilder(b=>b.ConfigureTestServices(s=>s.AddHttpClient("RoutingGeoapify").ConfigurePrimaryHttpMessageHandler(()=>handler)));var client=host.CreateClient();
        var p=await Preparar(data,client);var plan=await Plan(client,p);
        using var profile=await client.PutAsJsonAsync($"/api/reparto/perfiles-viales/{p.VehiculoId}",new{Version=0,Compatibilidad="DriveConfirmado",ConfirmarCompatibilidad=true,Motivo="Fixture compatible"});Assert.Equal(HttpStatusCode.OK,profile.StatusCode);
        clock.Now=new DateTimeOffset(FechaOperacion.ToDateTime(new TimeOnly(23,50)),TimeSpan.FromHours(-3));
        using var initial=await client.PostAsJsonAsync($"/api/rutas/{plan.Id}/estimaciones",new{ComandoId=Guid.NewGuid(),VersionRuta=plan.Version});Assert.Equal(HttpStatusCode.OK,initial.StatusCode);
        using var dispatched=await client.PostAsJsonAsync($"/api/rutas/{plan.Id}/despacho",new{plan.Version});Assert.Equal(HttpStatusCode.OK,dispatched.StatusCode);
        var route=(await client.GetFromJsonAsync<HojaRutaResponse>($"/api/mobile/rutas/{plan.Id}"))!;
        var f=new RecorridoFixture(data,factory,host,client,handler,clock,p,route);
        await f.Op("InicioJornada",new{});var stop=route.Paradas.Single();await f.Op("CargarBulto",new{BultoId=stop.Bultos.Single().Id},p.EnvioId,stop.EnvioVersion);await f.Op("ConfirmarCarga",new{});
        clock.Now=clock.Now.AddMinutes(9.5);return f;
    }
    private sealed class RecorridoClock(DateTimeOffset now):TimeProvider {public DateTimeOffset Now=now;public override DateTimeOffset GetUtcNow()=>Now.ToUniversalTime();}
    private sealed class RecorridoFixture(IntegrationDatabase data,IntegrationApiFactory factory,WebApplicationFactory<NexoRuta.Api.Controllers.AuthController> host,HttpClient client,RoadHandler handler,RecorridoClock clock,Preparado p,HojaRutaResponse route):IAsyncDisposable
    {
        public WebApplicationFactory<NexoRuta.Api.Controllers.AuthController> Host=host;public IntegrationDatabase Data=data;public HttpClient Client=client;public RoadHandler Handler=handler;public RecorridoClock Clock=clock;public Preparado Prepared=p;public HojaRutaResponse Route=route;private int sequence;
        public async Task Op(string type,object datos,Guid? envio=null,long? version=null)
        {using var r=await Client.PostAsJsonAsync($"/api/mobile/jornadas/{Route.JornadaId}/operaciones",new{Id=Guid.NewGuid(),Secuencia=++sequence,Tipo=type,EnvioId=envio,VersionEnvio=version,OcurridaEn=Clock.Now,Datos=datos});Assert.True(r.StatusCode==HttpStatusCode.OK,await r.Content.ReadAsStringAsync());var j=await r.Content.ReadFromJsonAsync<JsonElement>();Assert.True(j.GetProperty("resultado").GetString()=="Aplicada",j.GetRawText());}
        public Task Gps(DateTimeOffset measured,double precision=20)=>Op("Posicion",new{Latitud=-34.89,Longitud=-56.19,PrecisionMetros=precision,PosicionEnUtc=measured});
        public async Task<ActualizarEstimacionRequest> Command(){await using var db=Data.CreateDbContext();var h=await db.HojasRuta.AsNoTracking().SingleAsync(x=>x.Id==Route.Id);var o=await db.OcupacionesRuta.AsNoTracking().SingleAsync(x=>x.RutaId==Route.Id);return new(Guid.NewGuid(),h.Version,o.Id);}
        public async Task<EstimacionRutaResponse> Calculate(ActualizarEstimacionRequest command){using var r=await Client.PostAsJsonAsync($"/api/rutas/{Route.Id}/estimaciones/recorrido",command);Assert.True(r.StatusCode==HttpStatusCode.OK,await r.Content.ReadAsStringAsync());return(await r.Content.ReadFromJsonAsync<EstimacionRutaResponse>())!;}
        public async Task<EstimacionRutaResponse[]> History()=> (await Client.GetFromJsonAsync<EstimacionRutaResponse[]>($"/api/rutas/{Route.Id}/estimaciones"))!;
        public async Task<string> Physical(){await using var db=Data.CreateDbContext();return JsonSerializer.Serialize(new{H=await db.HojasRuta.AsNoTracking().Select(x=>new{x.Estado,x.Version,x.SalidaEnUtc,x.FinEnUtc,x.UltimaSecuencia}).ToArrayAsync(),B=await db.Bultos.AsNoTracking().Select(x=>new{x.Custodia,x.Version,x.RutaCustodiaId}).ToArrayAsync(),E=await db.Envios.AsNoTracking().Select(x=>new{x.Estado,x.Version}).ToArrayAsync(),O=await db.OcupacionesRuta.AsNoTracking().Select(x=>new{x.Id,x.Version,x.RetornoEnUtc,x.LiberadaEnUtc}).ToArrayAsync()});}
        public ValueTask DisposeAsync(){Client.Dispose();Host.Dispose();factory.Dispose();return ValueTask.CompletedTask;}
    }
}
