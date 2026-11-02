using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.IntegrationTests.Fixtures;
using static NexoRuta.IntegrationTests.Fixtures.RepartoHttpData;
namespace NexoRuta.IntegrationTests.Reparto;
public sealed class VehiculosIntegracionBackofficeHttpTests(PostgresFixture postgres):IClassFixture<PostgresFixture>
{
    [Fact]public async Task CurrentCheckoutPublishesFleetHistoryProfilesAndVehicleCrudWithIsolatedSchema()
    {
        var data=await postgres.CreateDatabaseAsync();using var factory=new IntegrationApiFactory(data,environment:"Development");using var client=factory.CreateHttpClient();var p=await Preparar(data,client);
        using var routeResponse=await client.PostAsJsonAsync("/api/rutas",Solicitud(p));Assert.Equal(HttpStatusCode.OK,routeResponse.StatusCode);var route=(await routeResponse.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
        var log=new List<object>();
        foreach(var path in new[]{"/api/reparto/flota","/api/reparto/perfiles-viales",$"/api/rutas/{route.Id}/estimaciones","/api/reparto/vehiculos"})
        {using var r=await client.GetAsync(path);Assert.True(r.StatusCode==HttpStatusCode.OK,await r.Content.ReadAsStringAsync());Assert.Equal(JsonValueKind.Array,(await r.Content.ReadFromJsonAsync<JsonElement>()).ValueKind);log.Add(new{Method="GET",Path=path,Status=(int)r.StatusCode});}
        using var create=await client.PutAsJsonAsync("/api/reparto/vehiculos",new{Id=Guid.Empty,Version=0,Matricula=" fixture-v ",PesoMaximoGramos=1000m,VolumenMaximoCm3=4000000m,Activo=true});Assert.Equal(HttpStatusCode.OK,create.StatusCode);var vehicle=(await create.Content.ReadFromJsonAsync<VehiculoContract>())!;Assert.Equal("FIXTURE-V",vehicle.Matricula);
        using var invalid=await client.PutAsJsonAsync("/api/reparto/vehiculos",new{vehicle.Id,vehicle.Version,vehicle.Matricula,PesoMaximoGramos=1.005m,VolumenMaximoCm3=vehicle.VolumenMaximoCm3,Activo=true});Assert.Equal(HttpStatusCode.BadRequest,invalid.StatusCode);
        var version=vehicle.Version;
        foreach(var active in new[]{false,true}){
            using var update=await client.PutAsJsonAsync("/api/reparto/vehiculos",new{vehicle.Id,Version=version,Matricula="FIXTURE-EDIT",PesoMaximoGramos=3000.01m,VolumenMaximoCm3=8000000.01m,Activo=active});Assert.Equal(HttpStatusCode.OK,update.StatusCode);var changed=(await update.Content.ReadFromJsonAsync<VehiculoContract>())!;Assert.Equal(active,changed.Activo);Assert.Equal(version+1,changed.Version);version=changed.Version;
        }
        using var stale=await client.PutAsJsonAsync("/api/reparto/vehiculos",new{vehicle.Id,vehicle.Version,Matricula="STALE",PesoMaximoGramos=100m,VolumenMaximoCm3=100m,Activo=false});Assert.Equal(HttpStatusCode.Conflict,stale.StatusCode);
        var list=(await client.GetFromJsonAsync<VehiculoContract[]>("/api/reparto/vehiculos"))!;var saved=list.Single(x=>x.Id==vehicle.Id);Assert.Equal("FIXTURE-EDIT",saved.Matricula);Assert.Equal(3000.01m,saved.PesoMaximoGramos);Assert.Equal(8000000.01m,saved.VolumenMaximoCm3);Assert.True(saved.Activo);Assert.Equal(version,saved.Version);
        await using var db=data.CreateDbContext();Assert.Empty(await db.OcupacionesRuta.ToArrayAsync());Assert.Equal("Planificada",(await db.HojasRuta.SingleAsync()).Estado.ToString());
        var file=Environment.GetEnvironmentVariable("NEXORUTA_CAPTURE_VEHICLE_PROBES");if(!string.IsNullOrEmpty(file))await File.WriteAllTextAsync(file,JsonSerializer.Serialize(log,new JsonSerializerOptions{WriteIndented=true}));
    }
}
