using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;
namespace NexoRuta.IntegrationTests.Webhooks;

public sealed class WebhooksFullStackTests(PostgresFixture postgres):IClassFixture<PostgresFixture>
{
    [WebhookBrowserFact]
    public async Task CommerceConfiguresRealHttpWorkersDeliverAndTrackingRecoversDurably()
    {
        var data=await postgres.CreateDatabaseAsync();using(var seed=data.CreateShippingApiFactory())using(seed.CreateHttpClient()){}
        var apiData=await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);var workerData=await WebhooksHttpTests.WorkerDatabase(data);
        var scratch=Path.Combine(Path.GetTempPath(),"nexoruta-webhook-e2e-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(scratch);
        var receiverAddress=$"http://127.0.0.1:{Port()}/";var commerceAddress=$"http://127.0.0.1:{Port()}/";
        var key="whsec_"+Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));var admin=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var settings=WebhooksHttpTests.Settings();settings["Webhooks:DevelopmentReceiverUrl"]=receiverAddress+"comercio/webhook";settings["DataProtection:KeysDirectory"]=Path.Combine(scratch,"shared-keys");
        using var factory=new IntegrationApiFactory(apiData,settings:settings,configureTestServices:s=>s.AddSingleton<IStartupFilter>(new ViewFailureFilter(scratch)));factory.UseKestrel(0);using var api=factory.CreateClient();
        await using var db=data.CreateDbContext();var op=(await db.Operadores.SingleAsync()).Id;var actor=await db.AccesosUsuario.SingleAsync(a=>a.ComercioId!=null);var email=(await db.Usuarios.SingleAsync(u=>u.Id==actor.UsuarioId)).Email!;
        var viewerEmail="webhook-viewer-"+Guid.NewGuid().ToString("N")+"@example.test";
        var viewer=new NexoRuta.Domain.Administracion.Usuario(viewerEmail);
        Assert.True((await AuthenticationTestData.UserManager(db).CreateAsync(viewer,AuthenticationTestData.Password)).Succeeded);
        var viewerRole=new NexoRuta.Domain.Administracion.Rol("webhook_viewer","Consulta de webhooks",null,actor.ComercioId);
        db.Add(viewerRole);db.Add(new NexoRuta.Domain.Administracion.RolPermiso(viewerRole.Id,(await db.Permisos.SingleAsync(p=>p.Codigo=="envios.consultar")).Id));
        db.Add(new NexoRuta.Domain.Administracion.AccesoUsuario(viewer.Id,null,actor.ComercioId,viewerRole));await db.SaveChangesAsync();
        var workspace=CompanionPortals.WorkspaceRoot;
        var receiverEnv=new Dictionary<string,string>{{"ASPNETCORE_URLS",receiverAddress},{"CommerceDemo__Enabled","true"},{"CommerceDemo__AdminPassword",admin},{"CommerceDemo__SigningSecrets__0",key},{"CommerceDemo__DatabasePath",Path.Combine(scratch,"inbox.db")},{"DataProtection__KeysDirectory",Path.Combine(scratch,"receiver-keys")}};
        var commerceEnv=new Dictionary<string,string>{{"ASPNETCORE_URLS",commerceAddress},{"Api__BaseAddress",api.BaseAddress!.ToString()},{"Tracking__BaseAddress",receiverAddress},{"DataProtection__KeysDirectory",Path.Combine(scratch,"commerce-keys")}};
        var workerEnv=new Dictionary<string,string>{{"Messaging__Transport","HttpWebhooks"},{"ConnectionStrings__Postgres",workerData.ConnectionString},{"DataProtection__KeysDirectory",settings["DataProtection:KeysDirectory"]!},{"Webhooks__DevelopmentReceiverUrl",receiverAddress+"comercio/webhook"},{"Webhooks__MaxAttempts","3"},{"Webhooks__RetryBaseSeconds","2"},{"Webhooks__RetryMaxSeconds","4"},{"Webhooks__TimeoutSeconds","1"}};
        OwnedProcess? tracking=null,workerA=null,workerB=null;
        Blackhole? blackhole=null;
        using var commerce=Start("frontend","NexoRuta.Commerce",commerceEnv);
        try
        {
            tracking=Start("frontend","NexoRuta.Tracking",receiverEnv);workerA=Start("backend","NexoRuta.Worker",workerEnv);workerB=Start("backend","NexoRuta.Worker",workerEnv);
            await Ready(commerceAddress);await Ready(receiverAddress);
            var nodeInfo=new ProcessStartInfo("node"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};nodeInfo.ArgumentList.Add(Path.Combine(workspace,"backend/tests/browser/webhooks.cjs"));
            foreach(var (k,v) in new Dictionary<string,string>{{"WH_API",api.BaseAddress!.ToString()},{"WH_COMMERCE",commerceAddress},{"WH_RECEIVER",receiverAddress},{"WH_EMAIL",email},{"WH_VIEWER_EMAIL",viewerEmail},{"WH_PASSWORD",AuthenticationTestData.Password},{"WH_SIGNING",key},{"WH_ADMIN",admin},{"WH_OPERATOR",op.ToString()},{"WH_IPC",scratch}})nodeInfo.Environment[k]=v;
            using var node=Process.Start(nodeInfo)!;var error=node.StandardError.ReadToEndAsync();using var limit=new CancellationTokenSource(TimeSpan.FromMinutes(4));
            try
            {
                while(await node.StandardOutput.ReadLineAsync(limit.Token) is {} line)
                {
                    switch(line)
                    {
                        case "stop-receiver":tracking!.Dispose();tracking=null;break;
                        case "start-stall":blackhole=new Blackhole(new Uri(receiverAddress).Port);break;
                        case "stop-stall":blackhole!.Dispose();blackhole=null;break;
                        case "start-receiver":tracking=Start("frontend","NexoRuta.Tracking",receiverEnv);await Ready(receiverAddress);break;
                        case "stop-workers":workerA!.Dispose();workerB!.Dispose();workerA=null;workerB=null;break;
                        case "start-workers":workerA=Start("backend","NexoRuta.Worker",workerEnv);workerB=Start("backend","NexoRuta.Worker",workerEnv);break;
                        default:continue;
                    }
                    await File.WriteAllTextAsync(Path.Combine(scratch,line+".done"),"done",limit.Token);
                }
                await node.WaitForExitAsync(limit.Token);
            }
            catch{if(!node.HasExited)node.Kill(true);throw;}
            Assert.True(node.ExitCode==0,await error);
            Assert.True((await new NexoRuta.Infrastructure.Webhooks.WebhookStore(db).Deliveries.CountAsync(d=>d.Estado=="Entregado"))>=6);
        }
        finally{blackhole?.Dispose();tracking?.Dispose();workerA?.Dispose();workerB?.Dispose();}
        OwnedProcess Start(string repo,string project,Dictionary<string,string> extra)
        {
            var content=Path.Combine(workspace,repo,"src",project);var dll=Path.Combine(content,"bin",CompanionPortals.Configuration,"net10.0",project+".dll");Assert.True(File.Exists(dll),"Build pinned companions before webhook E2E.");
            var info=new ProcessStartInfo("dotnet"){WorkingDirectory=content,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};info.ArgumentList.Add(dll);
            info.Environment["ASPNETCORE_ENVIRONMENT"]="Development";info.Environment["DOTNET_ENVIRONMENT"]="Development";info.Environment["OpenTelemetry__Enabled"]="false";
            foreach(var (k,v) in extra)info.Environment[k]=v;
            return new(Process.Start(info)!);
        }
    }
    private sealed class OwnedProcess:IDisposable
    {
        private readonly Process process;private readonly Task<string> output,error;
        public OwnedProcess(Process process){this.process=process;output=process.StandardOutput.ReadToEndAsync();error=process.StandardError.ReadToEndAsync();}
        public void Dispose(){if(!process.HasExited){process.Kill(true);process.WaitForExit(10000);}process.Dispose();}
    }
    private sealed class ViewFailureFilter(string directory):IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)=>app=>
        {
            app.Use(async(context,proceed)=>
            {
                if(context.Request.Path.StartsWithSegments("/api/webhooks") && context.Request.Method=="GET")
                {
                    if(File.Exists(Path.Combine(directory,"view-error"))){context.Response.StatusCode=503;return;}
                    if(File.Exists(Path.Combine(directory,"view-slow")))await Task.Delay(1200,context.RequestAborted);
                }
                await proceed();
            });next(app);
        };
    }
    private sealed class Blackhole:IDisposable
    {
        private readonly TcpListener listener;private readonly CancellationTokenSource stop=new();private readonly System.Collections.Concurrent.ConcurrentBag<TcpClient> clients=[];private readonly Task loop;
        internal Blackhole(int port){listener=new TcpListener(IPAddress.Loopback,port);listener.Server.SetSocketOption(SocketOptionLevel.Socket,SocketOptionName.ReuseAddress,true);listener.Start();loop=Task.Run(async()=>{try{while(!stop.IsCancellationRequested){var client=await listener.AcceptTcpClientAsync(stop.Token);clients.Add(client);}}catch(OperationCanceledException){}});}
        public void Dispose(){stop.Cancel();listener.Stop();foreach(var c in clients)c.Dispose();try{loop.GetAwaiter().GetResult();}catch(SocketException){}stop.Dispose();}
    }
    private static int Port(){var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();return port;}
    private static async Task Ready(string url)
    {
        using var http=new HttpClient();for(var i=0;i<150;i++){try{if((await http.GetAsync(url+"health/ready")).IsSuccessStatusCode)return;}catch(HttpRequestException){}await Task.Delay(100);}Assert.Fail("Isolated service did not become ready.");
    }
    private sealed class WebhookBrowserFact:FactAttribute
    {public WebhookBrowserFact(){if(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NEXORUTA_WEBHOOK_E2E_DIR")))Skip="Opt in with NEXORUTA_WEBHOOK_E2E_DIR and build the pinned frontend.";}}
}
