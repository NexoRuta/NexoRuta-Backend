using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Reparto;
namespace NexoRuta.IntegrationTests.Reparto;
public sealed class FleetStageMaintenanceTests(PostgresFixture postgres):IClassFixture<PostgresFixture>
{
    [Fact]public async Task ExactStageSchemaUsesRealAuthenticationFleetDomainIdempotencyAndBlocksNormalWriters()
    {
        var data=await postgres.CreateDatabaseAsync("20261101184716_StageFleetOccupations");Guid access,vehicle;
        await using(var db=data.CreateDbContext()){
            var initial=new NexoRuta.Infrastructure.Administracion.AccesoInicial("stage-commerce@example.test","Stage operator","Stage commerce","stage-operator@example.test");
            await AuthenticationTestData.Seeder(db,initial).SeedAsync();var a=await db.AccesosUsuario.SingleAsync(x=>x.OperadorId!=null);access=a.Id;var v=new Vehiculo(a.OperadorId!.Value,"STAGE",1000,1000);vehicle=v.Id;db.Add(v);await db.SaveChangesAsync();
        }
        var settings=new Dictionary<string,string?>{{"FleetTransition:StageOnly","true"},{"DatabaseInitialization:SeedData","false"},{"PlatformInitialization:Enabled","false"}};
        using var factory=new IntegrationApiFactory(data,settings:settings,replaceDbContext:false,seedOperationalRules:false);using var client=factory.CreateHttpClient();await client.SetBearerForAccessAsync(data,access);
        var fleet=Assert.Single((await client.GetFromJsonAsync<VehiculoFlotaResponse[]>("/api/reparto/flota"))!);Assert.Null(fleet.VerificacionInicialEnUtc);
        var cmd=new ComandoFlotaRequest(Guid.NewGuid(),null,fleet.Version,"Synthetic isolated fixture verification, not a physical attestation.");
        using var first=await client.PostAsJsonAsync($"/api/reparto/flota/{vehicle}/verificar-inicial",cmd);Assert.Equal(HttpStatusCode.OK,first.StatusCode);
        using var repeat=await client.PostAsJsonAsync($"/api/reparto/flota/{vehicle}/verificar-inicial",cmd);Assert.Equal(HttpStatusCode.OK,repeat.StatusCode);Assert.Equal(await first.Content.ReadAsStringAsync(),await repeat.Content.ReadAsStringAsync());
        Guid occupation;
        var options = new DbContextOptionsBuilder<NexoRutaDbContext>().UseNexoRutaPostgres(data.ConnectionString)
            .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCustomizer,FleetStageModelCustomizer>().Options;
        await using (var stageDb = new NexoRutaDbContext(options))
        {
            var v = await stageDb.Vehiculos.SingleAsync();
            stageDb.Add(new Repartidor(access,v.OperadorId,"Stage synthetic courier"));
            stageDb.Add(new ConfiguracionReparto(v.OperadorId,1,"America/Montevideo",30,false,true));
            var h = new HojaRuta(v.OperadorId,access,v,new DateOnly(2026,11,1),1);
            var o = new OcupacionRuta(h,access,DateTimeOffset.UtcNow); occupation=o.Id;
            stageDb.AddRange(h,o); await stageDb.SaveChangesAsync();
        }
        var occupied = Assert.Single((await client.GetFromJsonAsync<VehiculoFlotaResponse[]>("/api/reparto/flota"))!);
        Assert.Single(occupied.Ocupaciones);
        using var invalidReturn = await client.PostAsJsonAsync($"/api/reparto/flota/{vehicle}/retorno",
            new ComandoFlotaRequest(Guid.NewGuid(),occupation,0,"Synthetic route never started."));
        Assert.Equal(HttpStatusCode.Conflict,invalidReturn.StatusCode);
        using var normal=await client.PostAsJsonAsync("/api/rutas",new{});Assert.Equal(HttpStatusCode.ServiceUnavailable,normal.StatusCode);
        using var future=await client.GetAsync("/api/reparto/perfiles-viales");Assert.Equal(HttpStatusCode.ServiceUnavailable,future.StatusCode);
        await using(var db=data.CreateDbContext()){Assert.Single(await db.HechosFlota.ToArrayAsync());Assert.Equal("20261101184716_StageFleetOccupations",(await db.Database.GetAppliedMigrationsAsync()).Last());Assert.Equal(0,await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_name='HojasRuta' AND column_name='PlanTemporal'").SingleAsync());}
    }

    [Theory]
    [InlineData("DatabaseInitialization:ApplyMigrations")]
    [InlineData("DatabaseInitialization:SeedData")]
    [InlineData("PlatformInitialization:Enabled")]
    [InlineData("RoutingGeoapify:PermitirLlamadasReales")]
    public void StageRejectsAutomaticWritersAndExternalCalls(string key)
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string,string?> { [key] = "true" }).Build();
        Assert.Throws<InvalidOperationException>(() => NexoRuta.Api.Core.Operations.FleetStageMaintenance.ValidateConfiguration(configuration, false));
    }
    [Fact] public void StageRejectsUnboundedCliAndNonReconciliationEndpoints()
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        Assert.Throws<InvalidOperationException>(() => NexoRuta.Api.Core.Operations.FleetStageMaintenance.ValidateConfiguration(configuration, true));
        foreach (var path in new[] { "/api/rutas", "/api/reparto/flota/" + Guid.NewGuid() + "/despacho", "/api/auth/register" })
        {
            var request = new Microsoft.AspNetCore.Http.DefaultHttpContext().Request;
            request.Method = "POST"; request.Path = path;
            Assert.False(NexoRuta.Api.Core.Operations.FleetStageMaintenance.Allows(request));
        }
    }
    [Fact] public async Task StageRejectsLatestSchemaWithoutChangingHistory()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = new IntegrationApiFactory(data, settings: new Dictionary<string,string?> {
            ["FleetTransition:StageOnly"] = "true", ["DatabaseInitialization:SeedData"] = "false"
        }, replaceDbContext:false, seedOperationalRules:false);
        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateHttpClient());
        Assert.Contains("requires exactly Stage", error.Message);
        await using var db = data.CreateDbContext();
        Assert.Equal("20261101213104_AddRoadEstimationHistory", (await db.Database.GetAppliedMigrationsAsync()).Last());
    }
}
