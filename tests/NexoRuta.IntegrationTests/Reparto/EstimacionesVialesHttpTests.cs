using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using NexoRuta.IntegrationTests.Fixtures;
using static NexoRuta.IntegrationTests.Fixtures.RepartoHttpData;
namespace NexoRuta.IntegrationTests.Reparto;
public sealed partial class EstimacionesVialesHttpTests(PostgresFixture postgres):IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task DisabledCallsAndUnknownProfileAreExplicitAndCannotChangePhysicalFacts()
    {
        var data=await postgres.CreateDatabaseAsync();using var factory=data.CreateApiFactory();using var client=factory.CreateHttpClient();var p=await Preparar(data,client);
        using var profiles=await client.GetAsync("/api/reparto/perfiles-viales");Assert.Equal(HttpStatusCode.OK,profiles.StatusCode);
        var route=await Plan(client,p);using var first=await client.PostAsJsonAsync($"/api/rutas/{route.Id}/estimaciones",new{ComandoId=Guid.NewGuid(),VersionRuta=route.Version});Assert.Equal(HttpStatusCode.OK,first.StatusCode);
        var a=await first.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal("PerfilPendiente",a.GetProperty("motivo").GetString());
        using var unconfirmed=await client.PutAsJsonAsync($"/api/reparto/perfiles-viales/{p.VehiculoId}",new{Version=0,Compatibilidad="DriveConfirmado",Motivo="No confirmar implícitamente",ConfirmarCompatibilidad=false});Assert.Equal(HttpStatusCode.BadRequest,unconfirmed.StatusCode);
        using var set=await client.PutAsJsonAsync($"/api/reparto/perfiles-viales/{p.VehiculoId}",new{Version=0,Compatibilidad="DriveConfirmado",Motivo="Automóvil compatible revisado en fixture",ConfirmarCompatibilidad=true});Assert.Equal(HttpStatusCode.OK,set.StatusCode);
        using var second=await client.PostAsJsonAsync($"/api/rutas/{route.Id}/estimaciones",new{ComandoId=Guid.NewGuid(),VersionRuta=route.Version});Assert.Equal(HttpStatusCode.OK,second.StatusCode);
        var b=await second.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal("ProveedorNoConfigurado",b.GetProperty("estado").GetString());
        await using var db=data.CreateDbContext();Assert.Empty(await db.OcupacionesRuta.ToArrayAsync());Assert.Null((await db.HojasRuta.SingleAsync()).SalidaEnUtc);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ControlledHttpPersistsVersionedParallelHistoryAndIdempotency(bool restricted)
    {
        var data=await postgres.CreateDatabaseAsync();using var seed=data.CreateApiFactory();using(seed.CreateHttpClient()){}
        var runtime=restricted?await NexoRuta.IntegrationTests.Seguridad.RlsRestrictedHttpFlowTests.RestrictApiAsync(data):data;
        var settings=NexoRuta.IntegrationTests.Seguridad.RlsRestrictedHttpFlowTests.ApiSettings();settings["RoutingGeoapify:PermitirLlamadasReales"]="true";settings["RoutingGeoapify:ApiKey"]="TEST-NOT-A-CREDENTIAL";
        var handler=new RoadHandler();using var factory=new IntegrationApiFactory(runtime,settings:settings);
        using var host=factory.WithWebHostBuilder(b=>b.ConfigureTestServices(s=>s.AddHttpClient("RoutingGeoapify").ConfigurePrimaryHttpMessageHandler(()=>handler)));
        using var client=host.CreateClient();var p=await Preparar(data,client);var route=await Plan(client,p);
        using var profile=await client.PutAsJsonAsync($"/api/reparto/perfiles-viales/{p.VehiculoId}",new{Version=0,Compatibilidad="DriveConfirmado",Motivo="Automóvil de prueba compatible",ConfirmarCompatibilidad=true});Assert.Equal(HttpStatusCode.OK,profile.StatusCode);
        var command=new{ComandoId=Guid.NewGuid(),VersionRuta=route.Version};
        using var first=await client.PostAsJsonAsync($"/api/rutas/{route.Id}/estimaciones",command);Assert.True(first.StatusCode==HttpStatusCode.OK,await first.Content.ReadAsStringAsync());var one=await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(one.GetProperty("estado").GetString()=="Calculada",one.GetRawText()+" calls="+handler.Calls);Assert.Equal("Desconocida",one.GetProperty("vigencia").GetString());Assert.Equal(2,handler.Calls);
        var stop=one.GetProperty("paradas")[0];Assert.NotEqual(stop.GetProperty("llegadaManualEnUtc").GetDateTimeOffset(),stop.GetProperty("llegadaCalculadaEnUtc").GetDateTimeOffset());Assert.Equal(FechaOperacion.AddDays(1),DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(stop.GetProperty("llegadaCalculadaEnUtc").GetDateTimeOffset(),TimeZoneInfo.FindSystemTimeZoneById("America/Montevideo")).DateTime));
        using var replay=await client.PostAsJsonAsync($"/api/rutas/{route.Id}/estimaciones",command);Assert.Equal(HttpStatusCode.OK,replay.StatusCode);Assert.Equal(2,handler.Calls);
        using var second=await client.PostAsJsonAsync($"/api/rutas/{route.Id}/estimaciones",new{ComandoId=Guid.NewGuid(),VersionRuta=route.Version});Assert.Equal(HttpStatusCode.OK,second.StatusCode);
        var history=await client.GetFromJsonAsync<JsonElement[]>($"/api/rutas/{route.Id}/estimaciones");Assert.Equal(new long[]{2,1},history!.Select(x=>x.GetProperty("numeroVersion").GetInt64()));
        await using var db=data.CreateDbContext();var h=await db.HojasRuta.Include(x=>x.Paradas).SingleAsync();Assert.Equal(route.Version,h.Version);Assert.Equal(stop.GetProperty("llegadaManualEnUtc").GetDateTimeOffset(),h.Paradas.Single().LlegadaEstimadaUtc);Assert.Empty(await db.OcupacionesRuta.ToArrayAsync());Assert.Null(h.SalidaEnUtc);
        Assert.Equal(2,await db.Set<NexoRuta.Infrastructure.Reparto.EstimacionRegistro>().CountAsync());
        var e=await Assert.ThrowsAsync<Npgsql.PostgresException>(()=>db.Database.ExecuteSqlRawAsync("UPDATE \"EstimacionesRuta\" SET \"Motivo\"='changed'"));Assert.Equal("42501",e.SqlState);
        handler.Status=HttpStatusCode.TooManyRequests;using var failed=await client.PostAsJsonAsync($"/api/rutas/{route.Id}/estimaciones",new{ComandoId=Guid.NewGuid(),VersionRuta=route.Version});Assert.Equal(HttpStatusCode.OK,failed.StatusCode);
        var retained=await client.GetFromJsonAsync<JsonElement[]>($"/api/rutas/{route.Id}/estimaciones");Assert.Equal(3,retained!.Length);Assert.Equal("CuotaExcedida",retained[0].GetProperty("estado").GetString());Assert.Equal("Calculada",retained[1].GetProperty("estado").GetString());Assert.Equal("Calculada",retained[2].GetProperty("estado").GetString());
    }
    [Fact]
    public async Task ResponseBecomesObsoleteWhenProfileChangesDuringHttpWithoutHoldingTheOperatorLock()
    {
        var data=await postgres.CreateDatabaseAsync();var settings=new Dictionary<string,string?>{{"RoutingGeoapify:PermitirLlamadasReales","true"},{"RoutingGeoapify:ApiKey","TEST-ONLY"}};var handler=new RoadHandler();
        using var factory=new IntegrationApiFactory(data,settings:settings);using var host=factory.WithWebHostBuilder(b=>b.ConfigureTestServices(s=>s.AddHttpClient("RoutingGeoapify").ConfigurePrimaryHttpMessageHandler(()=>handler)));using var client=host.CreateClient();var p=await Preparar(data,client);var route=await Plan(client,p);
        using var confirm=await client.PutAsJsonAsync($"/api/reparto/perfiles-viales/{p.VehiculoId}",new{Version=0,Compatibilidad="DriveConfirmado",ConfirmarCompatibilidad=true,Motivo="Fixture compatible"});Assert.Equal(HttpStatusCode.OK,confirm.StatusCode);
        handler.Before=async ct=>{if(handler.Calls!=1)return;using var other=host.CreateClient();await other.SetBearerForAccessAsync(data,p.AccesoId);using var edit=await other.PutAsJsonAsync($"/api/reparto/perfiles-viales/{p.VehiculoId}",new{Version=1,Compatibilidad="NoCompatible",ConfirmarCompatibilidad=false,Motivo="Cambio concurrente controlado"},ct);Assert.Equal(HttpStatusCode.OK,edit.StatusCode);};
        using var response=await client.PostAsJsonAsync($"/api/rutas/{route.Id}/estimaciones",new{ComandoId=Guid.NewGuid(),VersionRuta=route.Version});Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);
        await using var db=data.CreateDbContext();Assert.Empty(await db.Set<NexoRuta.Infrastructure.Reparto.EstimacionRegistro>().ToArrayAsync());Assert.Equal(route.Version,(await db.HojasRuta.SingleAsync()).Version);Assert.Empty(await db.OcupacionesRuta.ToArrayAsync());
    }
    [Fact]
    public async Task PersistentBudgetAndProviderQuotaFailWithoutApplyingPartialSchedules()
    {
        var data=await postgres.CreateDatabaseAsync();var settings=new Dictionary<string,string?>{{"RoutingGeoapify:PermitirLlamadasReales","true"},{"RoutingGeoapify:ApiKey","TEST-ONLY"},{"RoutingGeoapify:MaxSolicitudesDiaOperador","1"}};var handler=new RoadHandler();
        using var factory=new IntegrationApiFactory(data,settings:settings);using var host=factory.WithWebHostBuilder(b=>b.ConfigureTestServices(s=>s.AddHttpClient("RoutingGeoapify").ConfigurePrimaryHttpMessageHandler(()=>handler)));using var client=host.CreateClient();var p=await Preparar(data,client);var route=await Plan(client,p);
        using var profile=await client.PutAsJsonAsync($"/api/reparto/perfiles-viales/{p.VehiculoId}",new{Version=0,Compatibilidad="DriveConfirmado",ConfirmarCompatibilidad=true,Motivo="Fixture"});Assert.Equal(HttpStatusCode.OK,profile.StatusCode);
        using var estimate=await client.PostAsJsonAsync($"/api/rutas/{route.Id}/estimaciones",new{ComandoId=Guid.NewGuid(),VersionRuta=route.Version});Assert.Equal(HttpStatusCode.OK,estimate.StatusCode);var result=await estimate.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal("CuotaExcedida",result.GetProperty("estado").GetString());Assert.Empty(result.GetProperty("paradas").EnumerateArray());Assert.Equal(1,handler.Calls);
        using var again=await client.PostAsJsonAsync($"/api/rutas/{route.Id}/estimaciones",new{ComandoId=Guid.NewGuid(),VersionRuta=route.Version});Assert.Equal(HttpStatusCode.OK,again.StatusCode);Assert.Equal(1,handler.Calls);
        await using var db=data.CreateDbContext();Assert.Null((await db.HojasRuta.SingleAsync()).SalidaEnUtc);Assert.Empty(await db.OcupacionesRuta.ToArrayAsync());
    }
    [Fact]
    public async Task ForeignOperatorAndCommerceCannotReadConfigureOrEstimateAnotherRouteAndRlsHidesHistory()
    {
        var data=await postgres.CreateDatabaseAsync();using var factory=data.CreateApiFactory();using var client=factory.CreateHttpClient();var p=await Preparar(data,client);var route=await Plan(client,p);
        using var estimate=await client.PostAsJsonAsync($"/api/rutas/{route.Id}/estimaciones",new{ComandoId=Guid.NewGuid(),VersionRuta=route.Version});Assert.Equal(HttpStatusCode.OK,estimate.StatusCode);
        Guid otherOp;Guid otherAccess;
        await using(var db=data.CreateDbContext())
        {
            var op=new NexoRuta.Domain.Administracion.Operador("Isolated road operator");otherOp=op.Id;var email=$"road-{Guid.NewGuid():N}@example.test";var user=new NexoRuta.Domain.Administracion.Usuario(email,AuthenticationTestData.Hash(email));var role=new NexoRuta.Domain.Administracion.Rol("ROAD-TEST","Road",op.Id,null);db.AddRange(op,user,role);
            foreach(var perm in await db.Permisos.Where(x=>x.Codigo=="envios.gestionar"||x.Codigo=="configuracion.gestionar").ToArrayAsync())db.Add(new NexoRuta.Domain.Administracion.RolPermiso(role.Id,perm.Id));await db.SaveChangesAsync();var a=new NexoRuta.Domain.Administracion.AccesoUsuario(user.Id,op.Id,null,role);db.Add(a);await db.SaveChangesAsync();otherAccess=a.Id;
        }
        await client.SetBearerForAccessAsync(data,otherAccess);using var foreign=await client.GetAsync($"/api/rutas/{route.Id}/estimaciones");Assert.Equal(HttpStatusCode.NotFound,foreign.StatusCode);using var calculate=await client.PostAsJsonAsync($"/api/rutas/{route.Id}/estimaciones",new{ComandoId=Guid.NewGuid(),VersionRuta=route.Version});Assert.Equal(HttpStatusCode.NotFound,calculate.StatusCode);
        using var foreignUpdate=await client.PostAsJsonAsync($"/api/rutas/{route.Id}/estimaciones/recorrido",new{ComandoId=Guid.NewGuid(),VersionRuta=route.Version,OcupacionId=Guid.NewGuid()});Assert.Equal(HttpStatusCode.NotFound,foreignUpdate.StatusCode);
        using var profile=await client.PutAsJsonAsync($"/api/reparto/perfiles-viales/{p.VehiculoId}",new{Version=0,Compatibilidad="DriveConfirmado",ConfirmarCompatibilidad=true,Motivo="Foreign"});Assert.Equal(HttpStatusCode.NotFound,profile.StatusCode);
        var runtime=await NexoRuta.IntegrationTests.Seguridad.RlsRestrictedHttpFlowTests.RestrictApiAsync(data);await using var conn=new Npgsql.NpgsqlConnection(runtime.ConnectionString);await conn.OpenAsync();await using var tx=await conn.BeginTransactionAsync();await using(var ctx=new Npgsql.NpgsqlCommand("SELECT set_config('nexoruta.actor_kind','operador',true),set_config('nexoruta.org_id',@op,true)",conn,tx)){ctx.Parameters.AddWithValue("op",otherOp.ToString());await ctx.ExecuteNonQueryAsync();}await using(var count=new Npgsql.NpgsqlCommand("SELECT count(*) FROM \"EstimacionesRuta\"",conn,tx))Assert.Equal(0L,await count.ExecuteScalarAsync());await using(var positions=new Npgsql.NpgsqlCommand("SELECT count(*) FROM \"OperacionesJornada\"",conn,tx))Assert.Equal(0L,await positions.ExecuteScalarAsync());await tx.RollbackAsync();
        await using(var db=data.CreateDbContext()){await client.SetBearerForAccessAsync(data,(await db.AccesosUsuario.SingleAsync(x=>x.ComercioId!=null)).Id);}using var denied=await client.GetAsync($"/api/rutas/{route.Id}/estimaciones");Assert.Equal(HttpStatusCode.Forbidden,denied.StatusCode);
    }
    [Fact]
    public async Task RoadContractHasOwnedDtosAndPublishesProfileAndExplicitCalculationEndpoints()
    {
        var data=await postgres.CreateDatabaseAsync();using var factory=new IntegrationApiFactory(data,environment:"Development");using var client=factory.CreateHttpClient();
        var json=await client.GetStringAsync("/openapi/v1.json");using var doc=JsonDocument.Parse(json);var paths=doc.RootElement.GetProperty("paths");foreach(var path in new[]{"/api/rutas/{id}/estimaciones","/api/rutas/{id}/estimaciones/recorrido","/api/reparto/perfiles-viales","/api/reparto/perfiles-viales/{id}"})Assert.True(paths.TryGetProperty(path,out _));
        var schemas=doc.RootElement.GetProperty("components").GetProperty("schemas");Assert.True(schemas.TryGetProperty("EstimacionRutaResponse",out _));var capture=Environment.GetEnvironmentVariable("NEXORUTA_CAPTURE_OPENAPI");if(!string.IsNullOrEmpty(capture))await File.WriteAllTextAsync(capture,json);
    }
    [Fact]
    public async Task GlobalBudgetIsExclusiveAcrossConcurrentConnectionsAndCannotBeReservedForAnotherTenant()
    {
        var data=await postgres.CreateDatabaseAsync();using var seed=data.CreateApiFactory();using var client=seed.CreateHttpClient();var p=await Preparar(data,client);
        Guid op;await using(var db=data.CreateDbContext())op=(await db.AccesosUsuario.SingleAsync(a=>a.Id==p.AccesoId)).OperadorId!.Value;
        var runtime=await NexoRuta.IntegrationTests.Seguridad.RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        async Task<object?> Reserve(Guid target)
        {
            await using var c=new Npgsql.NpgsqlConnection(runtime.ConnectionString);await c.OpenAsync();await using var t=await c.BeginTransactionAsync();await using(var ctx=new Npgsql.NpgsqlCommand("SELECT set_config('nexoruta.actor_kind','operador',true),set_config('nexoruta.org_id',@op,true)",c,t)){ctx.Parameters.AddWithValue("op",op.ToString());await ctx.ExecuteNonQueryAsync();}
            await using var cmd=new Npgsql.NpgsqlCommand("SELECT nexoruta_app.reserve_routing_request(@op,1,1,4)",c,t);cmd.Parameters.AddWithValue("op",target);var value=await cmd.ExecuteScalarAsync();await t.CommitAsync();return value;
        }
        var values=await Task.WhenAll(Reserve(op),Reserve(op));Assert.Single(values,v=>v is double);Assert.Single(values,v=>v is DBNull);
        var denied=await Assert.ThrowsAsync<Npgsql.PostgresException>(()=>Reserve(Guid.NewGuid()));Assert.Equal("42501",denied.SqlState);
    }
    private sealed class RoadHandler:HttpMessageHandler
    {
        public int Calls;public HttpStatusCode Status=HttpStatusCode.OK;public Func<CancellationToken,Task>? Before;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {Interlocked.Increment(ref Calls);Assert.Equal("api.geoapify.com",request.RequestUri!.Host);Assert.Contains("mode=drive",request.RequestUri.Query);Assert.Contains("traffic=free_flow",request.RequestUri.Query);if(Before is not null)await Before(ct);return new(Status){Content=Status==HttpStatusCode.OK?JsonContent.Create(new{results=new[]{new{distance_units="meters",legs=new[]{new{time=600d,distance=1000d}}}}}):new StringContent("private upstream error")};}
    }
    private static async Task<(Guid Id,long Version)> Plan(HttpClient client,Preparado p)
    {
        using var save=await client.PutAsJsonAsync("/api/reparto/base-operativa",new{Version=0,Direccion="Base sintética",ServicioPredeterminadoMinutos=10,MargenRetornoMinutos=7});Assert.Equal(HttpStatusCode.OK,save.StatusCode);
        using var confirm=await client.PostAsJsonAsync("/api/reparto/base-operativa/confirmacion",new{Version=1,Latitud=-34.9,Longitud=-56.2});Assert.Equal(HttpStatusCode.OK,confirm.StatusCode);
        var departure=new DateTimeOffset(FechaOperacion.ToDateTime(new TimeOnly(23,50)),TimeSpan.FromHours(-3));
        using var route=await client.PostAsJsonAsync("/api/rutas",new{RepartidorAccesoId=p.AccesoId,p.VehiculoId,Fecha=FechaOperacion,SalidaPlanificadaEnUtc=departure,Paradas=new[]{new{p.EnvioId,LlegadaEstimadaUtc=departure.AddHours(1)}}});Assert.Equal(HttpStatusCode.OK,route.StatusCode);
        var j=await route.Content.ReadFromJsonAsync<JsonElement>();return(j.GetProperty("id").GetGuid(),j.GetProperty("version").GetInt64());
    }
}
