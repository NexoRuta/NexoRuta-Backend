using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Infrastructure.Webhooks;
using NexoRuta.IntegrationTests.Fixtures;
namespace NexoRuta.IntegrationTests.Webhooks;

/// <summary>Opt-in synthetic real Tracking + restricted real worker for existing parcel/pickup scenarios.</summary>
internal sealed class WebhookAssay : IAsyncDisposable
{
    internal string Url {get;}
    private readonly string scratch=Path.Combine(Path.GetTempPath(),"nr-webhook-contracts-"+Guid.NewGuid().ToString("N"));
    private readonly string secret="whsec_"+Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private readonly List<Process> processes=[];
    internal WebhookAssay()
    {var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();Url=$"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/comercio/webhook";listener.Stop();}
    internal static bool Enabled=>!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NEXORUTA_WEBHOOK_E2E_DIR"));
    internal void Configure(Dictionary<string,string?> settings)
    {settings["Webhooks:DevelopmentReceiverUrl"]=Url;settings["DataProtection:KeysDirectory"]=Path.Combine(scratch,"shared-keys");}
    internal async Task Subscribe(IntegrationDatabase data,HttpClient api)
    {
        await using var db=data.CreateDbContext();var commerce=await db.AccesosUsuario.SingleAsync(a=>a.ComercioId!=null);var op=(await db.Operadores.SingleAsync()).Id;
        await api.SetBearerForAccessAsync(data,commerce.Id);
        using var response=await api.PutAsJsonAsync($"/api/webhooks/suscripciones/{Guid.NewGuid()}",new NexoRuta.Api.Features.Webhooks.SaveWebhook(op,Url,["OperacionActualizada","BultoActualizado","NuevaVisita","RetiroSolicitado","RetiroRetirado","RetiroEntregadoComercio"],2,true,0,secret));Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());
    }
    internal async Task DeliverAndAssert(IntegrationDatabase data,string name,params string[] required)
    {
        var workspace=CompanionPortals.WorkspaceRoot;var worker=await WebhooksHttpTests.WorkerDatabase(data);
        Start("frontend","NexoRuta.Tracking",new(){{"ASPNETCORE_URLS",new Uri(Url).GetLeftPart(UriPartial.Authority)},{"CommerceDemo__Enabled","true"},{"CommerceDemo__AdminPassword",Convert.ToHexString(RandomNumberGenerator.GetBytes(32))},{"CommerceDemo__SigningSecrets__0",secret},{"CommerceDemo__DatabasePath",Path.Combine(scratch,"inbox.db")},{"DataProtection__KeysDirectory",Path.Combine(scratch,"tracking-keys")}});
        using(var http=new HttpClient())for(var i=0;i<100;i++){try{if((await http.GetAsync(new Uri(new Uri(Url),"/health/ready"))).IsSuccessStatusCode)break;}catch(HttpRequestException){}await Task.Delay(100);}
        Start("backend","NexoRuta.Worker",new(){{"Messaging__Transport","HttpWebhooks"},{"ConnectionStrings__Postgres",worker.ConnectionString},{"DataProtection__KeysDirectory",Path.Combine(scratch,"shared-keys")},{"Webhooks__DevelopmentReceiverUrl",Url},{"Webhooks__RetryBaseSeconds","1"}});
        await using var db=data.CreateDbContext();WebhookDelivery[] rows=[];
        for(var i=0;i<300;i++){rows=await new WebhookStore(db).Deliveries.ToArrayAsync();if(rows.Length>0 && rows.All(d=>d.Estado=="Entregado"))break;await Task.Delay(100);}
        Assert.NotEmpty(rows);Assert.All(rows,d=>{Assert.Equal("Entregado",d.Estado);Assert.Equal(200,d.CodigoHttp);});
        foreach(var type in required)Assert.Contains(rows,d=>d.Tipo==type);
        var visits=rows.Where(d=>d.Tipo=="NuevaVisita").Select(d=>(d.EnvioId,JsonDocument.Parse(d.Payload).RootElement.GetProperty("visita").GetProperty("rutaId").GetGuid())).ToArray();Assert.Equal(visits.Length,visits.Distinct().Count());
        if(name=="pickup"){var final=Assert.Single(rows,d=>d.Tipo=="RetiroEntregadoComercio");var progress=JsonDocument.Parse(final.Payload).RootElement.GetProperty("progreso");Assert.Equal(progress.GetProperty("total").GetInt32(),progress.GetProperty("entregados").GetInt32());}
        if(name=="partial")Assert.Contains(rows,d=>JsonDocument.Parse(d.Payload).RootElement.GetProperty("estado").GetString()=="EntregadoParcialmente");
        var output=Environment.GetEnvironmentVariable("NEXORUTA_WEBHOOK_E2E_DIR")!;Directory.CreateDirectory(output);
        // Exact synthetic payloads transmitted and accepted by the real receiver; no secrets or personal data.
        await File.WriteAllTextAsync(Path.Combine(output,name+"-contracts.json"),"["+string.Join(",",rows.Select(d=>d.Payload))+"]");
        void Start(string repo,string project,Dictionary<string,string> extra)
        {
            var content=Path.Combine(workspace,repo,"src",project);var dll=Path.Combine(content,"bin",CompanionPortals.Configuration,"net10.0",project+".dll");var info=new ProcessStartInfo("dotnet"){WorkingDirectory=content,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};info.ArgumentList.Add(dll);info.Environment["ASPNETCORE_ENVIRONMENT"]="Development";info.Environment["DOTNET_ENVIRONMENT"]="Development";info.Environment["OpenTelemetry__Enabled"]="false";foreach(var(k,v)in extra)info.Environment[k]=v;
            var p=Process.Start(info)!;processes.Add(p);_ = p.StandardOutput.ReadToEndAsync();_ = p.StandardError.ReadToEndAsync();
        }
    }
    public ValueTask DisposeAsync(){foreach(var p in processes){if(!p.HasExited){p.Kill(true);p.WaitForExit(10000);}p.Dispose();}return ValueTask.CompletedTask;}
}
