using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexoRuta.Application.Envios.Interfaces;
using NexoRuta.Application.Envios.Dtos;
using NexoRuta.Domain.Administracion;
using Npgsql;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;
using static NexoRuta.IntegrationTests.Fixtures.RepartoHttpData;
namespace NexoRuta.IntegrationTests.Reparto;
public sealed class BasePlanificacionHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedBaseAndTemporalSnapshotSurviveBaseEditsAndCrossMidnight(bool restricted)
    {
        var data = await postgres.CreateDatabaseAsync(); using var owner = data.CreateApiFactory(); using var seed = owner.CreateHttpClient();
        var runtime = restricted ? await RlsRestrictedHttpFlowTests.RestrictApiAsync(data) : data;
        using var factory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings()); using var client = factory.CreateHttpClient();
        var p = await Preparar(data, client);
        await Save(client, 0, "Base A", 12, 7);
        using var confirmed = await client.PostAsJsonAsync("/api/reparto/base-operativa/confirmacion", new { Version = 1, Latitud = -34.9, Longitud = -56.2 });
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        var departure = new DateTimeOffset(FechaOperacion.ToDateTime(new TimeOnly(23, 0)), TimeSpan.FromHours(-3));
        using var plan = await client.PostAsJsonAsync("/api/rutas", new { RepartidorAccesoId = p.AccesoId, p.VehiculoId, Fecha = FechaOperacion,
            SalidaPlanificadaEnUtc = departure, Paradas = new[] { new { p.EnvioId, LlegadaEstimadaUtc = departure.AddMinutes(75), ServicioMinutos = 20 } } });
        Assert.True(plan.StatusCode == HttpStatusCode.OK, await plan.Content.ReadAsStringAsync());
        var hoja = await plan.Content.ReadFromJsonAsync<JsonElement>(); var id = hoja.GetProperty("id").GetGuid();
        await Save(client, 2, "Otra base", 8, 9);
        var actual = await client.GetFromJsonAsync<JsonElement>("/api/reparto/base-operativa");
        Assert.Equal(JsonValueKind.Null, actual.GetProperty("confirmadaEnUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, actual.GetProperty("latitud").ValueKind);
        var saved = (await client.GetFromJsonAsync<JsonElement>($"/api/rutas?fecha={FechaOperacion:yyyy-MM-dd}"))[0];
        var temporal = saved.GetProperty("planTemporal");
        Assert.Equal("Base A", temporal.GetProperty("direccionBase").GetString()); Assert.Equal(2, temporal.GetProperty("baseVersion").GetInt64());
        Assert.Equal(-34.9, temporal.GetProperty("latitudBase").GetDouble());
        Assert.Equal(departure.ToUniversalTime(), temporal.GetProperty("salidaPlanificadaEnUtc").GetDateTimeOffset());
        Assert.Equal(12, temporal.GetProperty("servicioPredeterminadoMinutos").GetInt32()); Assert.Equal(7, temporal.GetProperty("margenRetornoMinutos").GetInt32());
        Assert.Equal(20, saved.GetProperty("paradas")[0].GetProperty("servicioMinutos").GetInt32());
        Assert.Equal("SinEstimacion", saved.GetProperty("estadoEstimacion").GetString());
        await using var db = data.CreateDbContext(); Assert.Null((await db.HojasRuta.SingleAsync()).SalidaEnUtc); Assert.Empty(await db.OcupacionesRuta.ToArrayAsync());
    }
    [Fact]
    public async Task UnconfiguredBaseDoesNotInventCoordinatesOrServiceAndInvalidInputsDoNotMutate()
    {
        var data = await postgres.CreateDatabaseAsync(); using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var p = await Preparar(data, client);
        using var plan = await client.PostAsJsonAsync("/api/rutas", Solicitud(p)); Assert.Equal(HttpStatusCode.OK, plan.StatusCode);
        var hoja = await plan.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("SinEstimacion", hoja.GetProperty("estadoEstimacion").GetString());
        Assert.Equal(JsonValueKind.Null, hoja.GetProperty("planTemporal").GetProperty("latitudBase").ValueKind);
        Assert.Equal(JsonValueKind.Null, hoja.GetProperty("paradas")[0].GetProperty("servicioMinutos").ValueKind);
        using var negative = await client.PutAsJsonAsync("/api/reparto/base-operativa", new { Version = 0, Direccion = "Base", ServicioPredeterminadoMinutos = -1 });
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
        await Save(client, 0, "Base sin confirmar", null, null);
        using var invalid = await client.PostAsJsonAsync("/api/reparto/base-operativa/confirmacion", new { Version = 1, Latitud = 91, Longitud = -56 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var valid = await client.PostAsJsonAsync("/api/reparto/base-operativa/confirmacion", new { Version = 1, Latitud = 0, Longitud = 0 });
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        using var stale = await client.PutAsJsonAsync("/api/reparto/base-operativa", new { Version = 0, Direccion = "No guardar" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        await using var db = data.CreateDbContext(); await client.SetBearerForAccessAsync(data, (await db.AccesosUsuario.SingleAsync(a => a.ComercioId != null)).Id);
        using var denied = await client.GetAsync("/api/reparto/base-operativa"); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }
    [Fact]
    public async Task ConcurrentCreationRequiresOneWinnerAndOneConflict()
    {
        var data=await postgres.CreateDatabaseAsync(); using var factory=data.CreateApiFactory(); using var client=factory.CreateHttpClient();
        await Preparar(data,client);
        var responses=await Task.WhenAll(client.PutAsJsonAsync("/api/reparto/base-operativa",new {Version=0,Direccion="A"}),client.PutAsJsonAsync("/api/reparto/base-operativa",new {Version=0,Direccion="B"}));
        Assert.Equal(new[]{HttpStatusCode.OK,HttpStatusCode.Conflict},responses.Select(r=>r.StatusCode).Order());
        foreach(var r in responses)r.Dispose();
    }
    [Fact]
    public async Task ProposalsAreReadOnlyAndStaleProposalCannotConfirmAnEditedAddress()
    {
        var data=await postgres.CreateDatabaseAsync();using var factory=data.CreateApiFactory();
        using var host=factory.WithWebHostBuilder(b=>b.ConfigureTestServices(s=>{s.RemoveAll<IGeocodificador>();s.AddSingleton<IGeocodificador>(new Geocoder());}));
        using var client=host.CreateClient();await Preparar(data,client);await Save(client,0,"A",5,2);
        using var proposal=await client.PostAsync("/api/reparto/base-operativa/propuestas",null);Assert.Equal(HttpStatusCode.OK,proposal.StatusCode);
        var json=await proposal.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal(1,json.GetProperty("version").GetInt64());
        await using var db=data.CreateDbContext();Assert.Null((await db.BasesOperativas.AsNoTracking().SingleAsync()).ConfirmadaEnUtc);
        await Save(client,1,"B",5,2);
        using var stale=await client.PostAsJsonAsync("/api/reparto/base-operativa/confirmacion",new {Version=1,Latitud=-34.9,Longitud=-56.2});Assert.Equal(HttpStatusCode.Conflict,stale.StatusCode);
        using var confirmed=await client.PostAsJsonAsync("/api/reparto/base-operativa/confirmacion",new {Version=2,Latitud=-34.9,Longitud=-56.2});Assert.Equal(HttpStatusCode.OK,confirmed.StatusCode);
        await Save(client,3,"B",8,9);var current=await db.BasesOperativas.AsNoTracking().SingleAsync();Assert.NotNull(current.ConfirmadaEnUtc);Assert.Equal(4,current.Version);
    }
    [Fact]
    public async Task ProviderFailureLeavesManualConfirmationAvailableAndTemporalRejectionIsAtomic()
    {
        var data=await postgres.CreateDatabaseAsync();using var factory=data.CreateApiFactory();using var client=factory.CreateHttpClient();var p=await Preparar(data,client);
        await Save(client,0,"A",5,2);
        using var unavailable=await client.PostAsync("/api/reparto/base-operativa/propuestas",null);Assert.Equal(HttpStatusCode.ServiceUnavailable,unavailable.StatusCode);
        using var manual=await client.PostAsJsonAsync("/api/reparto/base-operativa/confirmacion",new {Version=1,Latitud=-34.9,Longitud=-56.2});Assert.Equal(HttpStatusCode.OK,manual.StatusCode);
        using var rejected=await client.PostAsJsonAsync("/api/rutas",new {RepartidorAccesoId=p.AccesoId,p.VehiculoId,Fecha=FechaOperacion,SalidaPlanificadaEnUtc=LlegadaEstimada.AddHours(1),Paradas=new[]{new {p.EnvioId,LlegadaEstimadaUtc=LlegadaEstimada}}});
        Assert.Equal(HttpStatusCode.BadRequest,rejected.StatusCode);await using var db=data.CreateDbContext();Assert.Empty(await db.HojasRuta.ToArrayAsync());Assert.Empty(await db.OcupacionesRuta.ToArrayAsync());
        using var plan=await client.PostAsJsonAsync("/api/rutas",Solicitud(p));Assert.Equal(HttpStatusCode.OK,plan.StatusCode);
        var hoja=await plan.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal(5,hoja.GetProperty("paradas")[0].GetProperty("servicioMinutos").GetInt32());
        var ex=await Assert.ThrowsAsync<PostgresException>(()=>db.Database.ExecuteSqlRawAsync("UPDATE \"HojasRuta\" SET \"PlanTemporal\"=NULL"));Assert.Equal("42501",ex.SqlState);
    }
    [Fact]
    public async Task OwnOperatorScopeAndRlsPreventReadingOrUpdatingAnotherBase()
    {
        var data=await postgres.CreateDatabaseAsync();using var factory=data.CreateApiFactory();using var client=factory.CreateHttpClient();await Preparar(data,client);await Save(client,0,"Private A",5,2);
        Guid other;Guid access;
        await using(var db=data.CreateDbContext())
        {
            var op=new Operador("Other base operator");other=op.Id;var email=$"base-{Guid.NewGuid():N}@example.test";var user=new Usuario(email,AuthenticationTestData.Hash(email));
            var role=new Rol(CodigosRolesSistema.AdministradorOperador,"Base",op.Id,null);role.ProtegerComoRolSistema();db.AddRange(op,user,role);
            foreach(var permission in await db.Permisos.Where(x=>x.Codigo=="configuracion.gestionar").ToArrayAsync())db.Add(new RolPermiso(role.Id,permission.Id));
            await db.SaveChangesAsync();var a=new AccesoUsuario(user.Id,op.Id,null,role);access=a.Id;db.Add(a);await db.SaveChangesAsync();
        }
        await client.SetBearerForAccessAsync(data,access);using var missing=await client.GetAsync("/api/reparto/base-operativa");Assert.Equal(HttpStatusCode.NotFound,missing.StatusCode);await Save(client,0,"Own B",null,null);
        var runtime=await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);await using var c=new NpgsqlConnection(runtime.ConnectionString);await c.OpenAsync();await using var tx=await c.BeginTransactionAsync();
        await using(var ctx=new NpgsqlCommand("SELECT set_config('nexoruta.actor_kind','operador',true),set_config('nexoruta.org_id',@op,true)",c,tx)){ctx.Parameters.AddWithValue("op",other.ToString());await ctx.ExecuteNonQueryAsync();}
        await using(var count=new NpgsqlCommand("SELECT count(*) FROM \"BasesOperativas\"",c,tx))Assert.Equal(1L,await count.ExecuteScalarAsync());
        await using(var update=new NpgsqlCommand("UPDATE \"BasesOperativas\" SET \"Direccion\"='Intrusion',\"Version\"=\"Version\"+1 WHERE \"Direccion\"='Private A'",c,tx))Assert.Equal(0,await update.ExecuteNonQueryAsync());
        await tx.RollbackAsync();await using var owner=data.CreateDbContext();Assert.Equal("Private A",(await owner.BasesOperativas.SingleAsync(b=>b.OperadorId!=other)).Direccion);
    }
    [Fact]
    public async Task AdditiveMigrationLeavesHistoricalSheetsWithoutInventedBaseOrDeparture()
    {
        var data=await postgres.CreateDatabaseAsync("20261101185119_EnforceFleetOccupationExclusivity");
        using var factory=data.CreateApiFactory();using var client=factory.CreateHttpClient();
        await using(var setup=data.CreateDbContext())
        {
            var access=await setup.AccesosUsuario.SingleAsync(a=>a.OperadorId!=null);
            await client.SetBearerForAccessAsync(data,access.Id);
            using var profile=await client.PutAsJsonAsync($"/api/repartidores/{access.Id}",new {Nombre="Historical driver",Activo=true});
            Assert.Equal(HttpStatusCode.OK,profile.StatusCode);
            using var vehicle=await client.PutAsJsonAsync("/api/reparto/vehiculos",new {Matricula="HIS1234",PesoMaximoGramos=100000m,VolumenMaximoCm3=1000000m,Activo=true});
            Assert.Equal(HttpStatusCode.OK,vehicle.StatusCode);
        }
        await using var db=data.CreateDbContext();var v=await db.Vehiculos.SingleAsync();var id=Guid.NewGuid();var day=new DateOnly(2020,1,2);var jornada=Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "HojasRuta" ("Id","OperadorId","RepartidorAccesoId","VehiculoId","VehiculoMatricula","VehiculoPesoMaximoGramos","VehiculoVolumenMaximoCm3","JornadaId","Fecha","ConfiguracionVersion","Version","ReservaRecursosActiva","Estado","UltimaSecuencia")
            VALUES ({id},{v.OperadorId},{(await db.AccesosUsuario.SingleAsync(a=>a.OperadorId!=null)).Id},{v.Id},{v.Matricula},{v.PesoMaximoGramos},{v.VolumenMaximoCm3},{jornada},{day},1,0,false,'Finalizada',0)
            """);
        await db.Database.MigrateAsync();var historical=await db.HojasRuta.SingleAsync();Assert.Equal(id,historical.Id);Assert.Null(historical.PlanTemporal);Assert.Null(historical.SalidaEnUtc);Assert.Empty(await db.BasesOperativas.ToArrayAsync());
        Assert.Empty(await db.OcupacionesRuta.ToArrayAsync());
    }
    [Fact]
    public async Task ReassignmentUsesDestinationSnapshotServiceAndDoesNotRewriteInitialOrder()
    {
        var data=await postgres.CreateDatabaseAsync();using var factory=data.CreateApiFactory();using var client=factory.CreateHttpClient();var p=await Preparar(data,client);
        await Save(client,0,"A",5,2);using var source=await client.PostAsJsonAsync("/api/rutas",Solicitud(p));Assert.Equal(HttpStatusCode.OK,source.StatusCode);var tomorrow=FechaOperacion.AddDays(1);
        using var plan=await client.PostAsJsonAsync("/api/rutas",new {RepartidorAccesoId=p.AccesoId,p.VehiculoId,Fecha=tomorrow,Paradas=Array.Empty<object>()});Assert.Equal(HttpStatusCode.OK,plan.StatusCode);
        var original=await plan.Content.ReadFromJsonAsync<JsonElement>();var id=original.GetProperty("id").GetGuid();await Save(client,1,"B",9,3);
        await using var db=data.CreateDbContext();var version=await db.Envios.Where(e=>e.Id==p.EnvioId).Select(e=>e.Version).SingleAsync();
        var arrival=new DateTimeOffset(tomorrow.AddDays(1).ToDateTime(new TimeOnly(0,15)),TimeSpan.FromHours(-3));
        using var invalid=await client.PostAsJsonAsync($"/api/envios/{p.EnvioId}/reasignacion",new {RutaDestinoId=id,VersionRutaDestino=0,VersionEnvio=version,LlegadaEstimadaUtc=arrival,Motivo="Controlled reassignment",ServicioMinutos=-1});Assert.Equal(HttpStatusCode.BadRequest,invalid.StatusCode);
        Assert.Single(await db.ParadasRuta.ToArrayAsync());Assert.Equal(version,await db.Envios.Where(e=>e.Id==p.EnvioId).Select(e=>e.Version).SingleAsync());
        using var assigned=await client.PostAsJsonAsync($"/api/envios/{p.EnvioId}/reasignacion",new {RutaDestinoId=id,VersionRutaDestino=0,VersionEnvio=version,LlegadaEstimadaUtc=arrival,Motivo="Controlled reassignment"});Assert.True(assigned.StatusCode==HttpStatusCode.OK,await assigned.Content.ReadAsStringAsync());
        var current=await assigned.Content.ReadFromJsonAsync<JsonElement>();Assert.Equal(5,current.GetProperty("paradas")[0].GetProperty("servicioMinutos").GetInt32());Assert.Equal(original.GetProperty("planTemporal").GetRawText(),current.GetProperty("planTemporal").GetRawText());Assert.Null((await db.HojasRuta.SingleAsync(h=>h.Id==id)).SalidaEnUtc);Assert.Empty(await db.OcupacionesRuta.ToArrayAsync());
    }
    private sealed class Geocoder:IGeocodificador
    {
        public string Atribucion=>"Controlled geocoder";
        public Task<IReadOnlyList<PropuestaDestinoDatos>> BuscarAsync(string direccion,CancellationToken ct, bool autocompletar = false)=>Task.FromResult<IReadOnlyList<PropuestaDestinoDatos>>([new(direccion,-34.9,-56.2)]);
    }
    private static async Task Save(HttpClient client, long version, string address, int? service, int? margin)
    {
        using var r = await client.PutAsJsonAsync("/api/reparto/base-operativa", new { Version = version, Direccion = address, ServicioPredeterminadoMinutos = service, MargenRetornoMinutos = margin });
        Assert.True(r.StatusCode == HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
    }
}
