using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.Api.Features.Reparto.Contracts;
using static NexoRuta.IntegrationTests.Fixtures.RepartoHttpData;
namespace NexoRuta.IntegrationTests.Reparto;
public sealed class VehiculosBackofficeBrowserTests(PostgresFixture postgres):IClassFixture<PostgresFixture>
{
    [VehicleBrowserFact]public async Task ChromiumTraversesCheckoutBackofficeRealApiAndEphemeralPostgres()
    {
        var data=await postgres.CreateDatabaseAsync();var queries=new ConcurrentQueue<Query>();using var factory=new IntegrationApiFactory(data,configureTestServices:s=>s.AddSingleton<IStartupFilter>(new Capture(queries)));factory.UseKestrel(0);
        using var api=factory.CreateClient();var p=await Preparar(data,api);
        using var plan=await api.PostAsJsonAsync("/api/rutas",Solicitud(p));Assert.Equal(HttpStatusCode.OK,plan.StatusCode);
        var workspace=CompanionPortals.WorkspaceRoot;var dll=Path.Combine(workspace,"frontend", "src", "NexoRuta.Backoffice", "bin", CompanionPortals.Configuration, "net10.0", "NexoRuta.Backoffice.dll");Assert.True(File.Exists(dll),"Build current Backoffice before opt-in browser test.");
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();var address=$"http://127.0.0.1:{port}/";
        var start=new ProcessStartInfo("dotnet"){WorkingDirectory=Path.Combine(workspace,"frontend/src/NexoRuta.Backoffice"),RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true};start.ArgumentList.Add(dll);start.Environment["ASPNETCORE_URLS"]=address;start.Environment["ASPNETCORE_ENVIRONMENT"]="Development";start.Environment["Api__BaseAddress"]=api.BaseAddress!.ToString();start.Environment["OpenTelemetry__Enabled"]="false";start.Environment["DataProtection__KeysDirectory"]=Path.Combine(Path.GetTempPath(),"NexoRuta-Vehicle-Browser-"+Guid.NewGuid().ToString("N"));
        using var backoffice=Process.Start(start)!;var stdout=backoffice.StandardOutput.ReadToEndAsync();var stderr=backoffice.StandardError.ReadToEndAsync();
        try{
            using var probe=new HttpClient();bool ready=false;for(int i=0;i<100;i++){try{if((await probe.GetAsync(address+"ready")).IsSuccessStatusCode){ready=true;break;}}catch(HttpRequestException){}if(backoffice.HasExited)break;await Task.Delay(100);}Assert.True(ready,"Isolated checkout Backoffice did not start.");
            var nodeStart=new ProcessStartInfo("node"){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true};nodeStart.ArgumentList.Add(Path.Combine(workspace,"frontend/tests/browser/cu06-vehiculos.cjs"));nodeStart.Environment["VEH_E2E_URL"]=address;nodeStart.Environment["VEH_E2E_ROUTE"]=(await plan.Content.ReadFromJsonAsync<HojaRutaResponse>())!.Id.ToString();
            await using(var db=data.CreateDbContext())nodeStart.Environment["VEH_E2E_EMAIL"]=await (from a in db.AccesosUsuario join u in db.Usuarios on a.UsuarioId equals u.Id where a.Id==p.AccesoId select u.Email).SingleAsync();nodeStart.Environment["VEH_E2E_PASSWORD"]=AuthenticationTestData.Password;
            using var node=Process.Start(nodeStart)!;var output=node.StandardOutput.ReadToEndAsync();var error=node.StandardError.ReadToEndAsync();using var limit=new CancellationTokenSource(TimeSpan.FromMinutes(2));try{await node.WaitForExitAsync(limit.Token);}catch(OperationCanceledException){node.Kill(true);throw;}Assert.True(node.ExitCode==0,await output+await error);
            await using(var db=data.CreateDbContext()){var vehicle=await db.Vehiculos.SingleAsync(v=>v.Matricula=="BROWSER-V");Assert.True(vehicle.Activo);Assert.Equal(3000.01m,vehicle.PesoMaximoGramos);Assert.Equal(8000000.01m,vehicle.VolumenMaximoCm3);Assert.Equal(2,vehicle.Version);Assert.Empty(await db.OcupacionesRuta.ToArrayAsync());}
            foreach(var path in new[]{"/api/reparto/flota","/api/reparto/perfiles-viales"})Assert.Contains(queries,x=>x.Path==path&&x.Method=="GET"&&x.Status==200);Assert.Contains(queries,x=>x.Path.EndsWith("/estimaciones")&&x.Status==200);
        }finally{if(!backoffice.HasExited)backoffice.Kill(true);await backoffice.WaitForExitAsync();var outDir=Environment.GetEnvironmentVariable("NEXORUTA_VEHICLES_E2E_DIR")!;Directory.CreateDirectory(outDir);await File.WriteAllTextAsync(Path.Combine(outDir,"api-queries.json"),JsonSerializer.Serialize(queries,new JsonSerializerOptions{WriteIndented=true}));await File.WriteAllTextAsync(Path.Combine(outDir,"backoffice-startup.log"),await stdout+await stderr);}
    }
    private sealed record Query(string Method,string Path,int Status);
    private sealed class Capture(ConcurrentQueue<Query> rows):IStartupFilter
    {public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)=>app=>{app.Use(async(ctx,continuation)=>{await continuation();var p=ctx.Request.Path.Value??"";if(p is "/api/reparto/flota" or "/api/reparto/perfiles-viales" or "/api/reparto/vehiculos"||p.EndsWith("/estimaciones"))rows.Enqueue(new(ctx.Request.Method,p,ctx.Response.StatusCode));});next(app);};}
    private sealed class VehicleBrowserFact:FactAttribute
    {public VehicleBrowserFact(){if(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NEXORUTA_VEHICLES_E2E_DIR")))Skip="Opt-in real local checkout/ephemeral database browser test.";}}
}
