using Microsoft.AspNetCore.Http;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;
namespace NexoRuta.IntegrationTests.Gestion;
public sealed class GestionBrowserTests(PostgresFixture postgres):IClassFixture<PostgresFixture>
{
    [BrowserFact]
    public async Task OperadorFiltraRevisaConfirmaYComercioConsultaConApiYPostgresReales()
    {
        var s=await GestionScenario.CreateAsync(postgres);Assert.Equal(9,await s.ReconcileAsync());
        var settings=RlsRestrictedHttpFlowTests.ApiSettings();
        using var factory=new IntegrationApiFactory(s.Runtime,settings:settings,configureTestServices:x=>x.AddSingleton<IStartupFilter>(new SlowManagement()));
        factory.UseKestrel(0);using var api=factory.CreateClient();
        var output=Path.Combine(Path.GetFullPath(Environment.GetEnvironmentVariable("NEXORUTA_GESTION_E2E_DIR")!),"run-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(output);
        var apps=new List<Process>();var logs=new List<Task<string>>();
        try{
            async Task<string> Start(string portal)
            {
                var directory=Path.Combine(CompanionPortals.WorkspaceRoot,"frontend/src/NexoRuta."+portal);
                var dll=Path.Combine(directory,"bin",CompanionPortals.Configuration,"net10.0","NexoRuta."+portal+".dll");Assert.True(File.Exists(dll),"Build the exact companion frontend before this browser test.");
                var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();var address=$"http://127.0.0.1:{port}";
                var start=new ProcessStartInfo("dotnet"){WorkingDirectory=directory,RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false};start.ArgumentList.Add(dll);
                start.Environment["ASPNETCORE_URLS"]=address;start.Environment["ASPNETCORE_ENVIRONMENT"]="Development";start.Environment["Api__BaseAddress"]=api.BaseAddress!.ToString();start.Environment["OpenTelemetry__Enabled"]="false";start.Environment["DataProtection__KeysDirectory"]=Path.Combine(Path.GetTempPath(),"nexoruta-management-keys-"+Guid.NewGuid().ToString("N"));
                var app=Process.Start(start)!;apps.Add(app);logs.Add(app.StandardOutput.ReadToEndAsync());logs.Add(app.StandardError.ReadToEndAsync());
                using var http=new HttpClient();for(var i=0;i<150;i++){try{if((await http.GetAsync(address+"/ready")).IsSuccessStatusCode)return address;}catch(HttpRequestException){}if(app.HasExited)break;await Task.Delay(100);}
                throw new InvalidOperationException("Owned synthetic portal did not start: "+portal);
            }
            var backoffice=await Start("Backoffice");var commerce=await Start("Commerce");
            var nodeStart=new ProcessStartInfo("node"){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false};nodeStart.ArgumentList.Add(Path.Combine(CompanionPortals.WorkspaceRoot,"backend/tests/browser/management.cjs"));
            foreach(var pair in new Dictionary<string,string>{["MANAGEMENT_BACKOFFICE"]=backoffice,["MANAGEMENT_COMMERCE"]=commerce,["MANAGEMENT_OPERATOR"]=s.OperatorA.OperadorId!.Value.ToString(),["MANAGEMENT_OTHER_OPERATOR"]=s.OperatorB.OperadorId!.Value.ToString(),["MANAGEMENT_COMMERCE_ID"]=s.CommerceA.ComercioId!.Value.ToString(),["MANAGEMENT_PASSWORD"]=AuthenticationTestData.Password,["MANAGEMENT_PARTIAL"]=s.PartialShipment.ToString()})nodeStart.Environment[pair.Key]=pair.Value;
            nodeStart.Environment["NEXORUTA_GESTION_E2E_DIR"]=output;
            using var node=Process.Start(nodeStart)!;var stdout=node.StandardOutput.ReadToEndAsync();var stderr=node.StandardError.ReadToEndAsync();
            using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(6));await node.WaitForExitAsync(timeout.Token);Assert.True(node.ExitCode==0,await stdout+await stderr);
        }finally{foreach(var app in apps){if(!app.HasExited)app.Kill(entireProcessTree:true);await app.WaitForExitAsync();app.Dispose();}await Task.WhenAll(logs);}
    }
    private sealed class SlowManagement:IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)=>app=>{
            app.Use(async(ctx,nextMiddleware)=>{if(ctx.Request.Path.StartsWithSegments("/api/gestion") && HttpMethods.IsGet(ctx.Request.Method))await Task.Delay(500,ctx.RequestAborted);await nextMiddleware();});next(app);
        };
    }
    private sealed class BrowserFact:FactAttribute
    {
        public BrowserFact(){if(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NEXORUTA_GESTION_E2E_DIR")))Skip="Set NEXORUTA_GESTION_E2E_DIR to run the real portals/API/Postgres acceptance circuit.";}
    }
}
