using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.IntegrationTests.Fixtures;
using static NexoRuta.IntegrationTests.Fixtures.RepartoHttpData;
namespace NexoRuta.IntegrationTests.Reparto;
public sealed class FleetBoundedTransitionTests(PostgresFixture postgres):IClassFixture<PostgresFixture>
{
    [Fact] public async Task FourExplicitStagesPermitCurrentApiWithoutInventingLegacyFacts()
    {
        var data = await postgres.CreateDatabaseAsync("20261030120000_ListImportedShipmentReferences");
        string[] migrations = ["20261101184716_StageFleetOccupations", "20261101185119_EnforceFleetOccupationExclusivity",
            "20261101203256_AddOperationalBaseAndTemporalPlan", "20261101213104_AddRoadEstimationHistory"];
        await using (var db = data.CreateDbContext())
        {
            var previous = "20261030120000_ListImportedShipmentReferences";
            foreach (var target in migrations)
            {
                var script = db.GetService<IMigrator>().GenerateScript(previous,target);
                await db.Database.ExecuteSqlRawAsync(script);
                Assert.Equal(target,(await db.Database.GetAppliedMigrationsAsync()).Last()); previous=target;
                Assert.Empty(await db.OcupacionesRuta.ToArrayAsync()); Assert.Empty(await db.HechosFlota.ToArrayAsync());
            }
        }
        using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var prepared = await Preparar(data,client); // Synthetic fixture only; not reconciliation of restored local records.
        using var planned = await client.PostAsJsonAsync("/api/rutas",Solicitud(prepared)); Assert.Equal(HttpStatusCode.OK,planned.StatusCode);
        var route = (await planned.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
        foreach (var path in new[] { "/api/reparto/vehiculos", "/api/reparto/flota", "/api/reparto/perfiles-viales", $"/api/rutas/{route.Id}/estimaciones" })
        {
            using var response = await client.GetAsync(path); Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        }
        var artifactRoot = Environment.GetEnvironmentVariable("NEXORUTA_REHEARSAL_ARTIFACTS");
        if (!string.IsNullOrEmpty(artifactRoot))
        {
            var restricted = await NexoRuta.IntegrationTests.Seguridad.RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
            var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../.."));
            var start = new System.Diagnostics.ProcessStartInfo("python") { WorkingDirectory=workspace, UseShellExecute=false,
                RedirectStandardOutput=true, RedirectStandardError=true, CreateNoWindow=true };
            start.ArgumentList.Add(Path.Combine(workspace,"scripts/cu06/probe-current.py"));
            start.Environment["CU06_FIXTURE_CONNECTION"]=restricted.ConnectionString;
            await using(var db=data.CreateDbContext())start.Environment["CU06_FIXTURE_EMAIL"]=await
                (from a in db.AccesosUsuario join u in db.Usuarios on a.UsuarioId equals u.Id where a.Id==prepared.AccesoId select u.Email).SingleAsync();
            start.Environment["CU06_FIXTURE_PASSWORD"]=AuthenticationTestData.Password;
            start.Environment["CU06_FIXTURE_ROUTE"]=route.Id.ToString();
            using var child=System.Diagnostics.Process.Start(start)!;
            var output=child.StandardOutput.ReadToEndAsync();var errors=child.StandardError.ReadToEndAsync();
            using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try { await child.WaitForExitAsync(timeout.Token); } catch(OperationCanceledException) { child.Kill(true); throw; }
            Assert.True(child.ExitCode==0,await output+await errors);
        }
    }
}
