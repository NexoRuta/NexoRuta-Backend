using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Domain.Envios;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;
using static NexoRuta.IntegrationTests.Fixtures.RepartoHttpData;
namespace NexoRuta.IntegrationTests.Reparto;
public sealed class RecepcionDepositoBrowserTests(PostgresFixture postgres):IClassFixture<PostgresFixture>
{
    [ReceiptBrowserFact]
    public async Task RealLabelDecodingAndBackofficeReceiptUseRestrictedApiAndIsolatedPostgres()
    {
        var output=Environment.GetEnvironmentVariable("NEXORUTA_RECEIPT_E2E_DIR")!;Directory.CreateDirectory(output);
        var data=await postgres.CreateDatabaseAsync();Guid access,shipment;string email;
        using(var setup=data.CreateShippingApiFactory())using(var client=setup.CreateHttpClient())
        {
            var p=await Preparar(data,client,recibir:false);access=p.AccesoId;
            await using var db=data.CreateDbContext();var comercio=await db.AccesosUsuario.SingleAsync(a=>a.ComercioId!=null);var actor=await db.AccesosUsuario.SingleAsync(a=>a.Id==access);
            email=(await db.Usuarios.SingleAsync(u=>u.Id==actor.UsuarioId)).Email;
            await client.SetBearerForAccessAsync(data,comercio.Id);
            using var created=await client.PostAsJsonAsync("/api/envios",new{OperadorId=actor.OperadorId,Servicio=ServiciosEnvioTestData.Declarar(actor.OperadorId),DestinatarioNombre="Browser receipt",DestinatarioEmail="browser-receipt@example.test",Direccion="Synthetic browser address",Bultos=Enumerable.Range(1,2).Select(i=>new{Codigo="BROWSER-RECEIPT-"+i,PesoGramos=500,LargoCentimetros=20,AnchoCentimetros=10,AltoCentimetros=5}).ToArray()});
            Assert.Equal(HttpStatusCode.Created,created.StatusCode);shipment=(await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        }
        var restricted=await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);var faults=new ReceiptFaults();
        using var apiFactory=new IntegrationApiFactory(restricted,settings:RlsRestrictedHttpFlowTests.ApiSettings(),configureTestServices:services=>services.AddSingleton<IStartupFilter>(new ReceiptFaultFilter(faults)));apiFactory.UseKestrel(0);using var api=apiFactory.CreateClient();await api.SetBearerForAccessAsync(data,access);
        using(var pdf=await api.GetAsync($"/api/envios/{shipment}/etiquetas"))
        {Assert.Equal(HttpStatusCode.OK,pdf.StatusCode);await File.WriteAllBytesAsync(Path.Combine(output,"etiqueta.pdf"),await pdf.Content.ReadAsByteArrayAsync());}
        var render=new ProcessStartInfo("pdftoppm"){RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var argument in new[]{"-f","1","-singlefile","-scale-to","1600","-png",Path.Combine(output,"etiqueta.pdf"),Path.Combine(output,"etiqueta")})render.ArgumentList.Add(argument);
        using(var process=Process.Start(render)!){await process.WaitForExitAsync();Assert.True(process.ExitCode==0,await process.StandardError.ReadToEndAsync());}
        var workspace=CompanionPortals.WorkspaceRoot;
        var dll=Path.Combine(workspace,"frontend", "src", "NexoRuta.Backoffice", "bin", CompanionPortals.Configuration, "net10.0", "NexoRuta.Backoffice.dll");Assert.True(File.Exists(dll));
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();var url=$"http://127.0.0.1:{port}/";
        var start=new ProcessStartInfo("dotnet"){WorkingDirectory=Path.Combine(workspace,"frontend/src/NexoRuta.Backoffice"),RedirectStandardOutput=true,RedirectStandardError=true};start.ArgumentList.Add(dll);
        start.Environment["ASPNETCORE_URLS"]=url;start.Environment["ASPNETCORE_ENVIRONMENT"]="Development";start.Environment["Api__BaseAddress"]=api.BaseAddress!.ToString();start.Environment["OpenTelemetry__Enabled"]="false";start.Environment["DataProtection__KeysDirectory"]=Path.Combine(output,"private-keys");
        using var backoffice=Process.Start(start)!;var stdout=backoffice.StandardOutput.ReadToEndAsync();var stderr=backoffice.StandardError.ReadToEndAsync();
        try
        {
            using var probe=new HttpClient();var ready=false;
            for(var i=0;i<100;i++){try{if((await probe.GetAsync(url+"ready")).IsSuccessStatusCode){ready=true;break;}}catch(HttpRequestException){}if(backoffice.HasExited)break;await Task.Delay(100);}
            Assert.True(ready,"Isolated Backoffice did not start.");
            var nodeStart=new ProcessStartInfo("node"){RedirectStandardOutput=true,RedirectStandardError=true};nodeStart.ArgumentList.Add(Path.Combine(workspace,"frontend/tests/browser/cu10-recepcion.cjs"));
            nodeStart.Environment["RECEIPT_E2E_API"]=api.BaseAddress!.ToString();
            nodeStart.Environment["RECEIPT_E2E_URL"]=url;nodeStart.Environment["RECEIPT_E2E_EMAIL"]=email;nodeStart.Environment["RECEIPT_E2E_PASSWORD"]=AuthenticationTestData.Password;
            await using(var db=data.CreateDbContext())nodeStart.Environment["RECEIPT_E2E_PARCELS"]=JsonSerializer.Serialize(await db.Bultos.Where(b=>b.EnvioId==shipment).OrderBy(b=>b.Codigo).Select(b=>b.Id).ToArrayAsync());
            using var node=Process.Start(nodeStart)!;var result=node.StandardOutput.ReadToEndAsync();var error=node.StandardError.ReadToEndAsync();using var limit=new CancellationTokenSource(TimeSpan.FromMinutes(4));
            try{await node.WaitForExitAsync(limit.Token);}catch(OperationCanceledException){node.Kill(true);throw;}
            Assert.True(node.ExitCode==0,await result+await error);
            Assert.Equal(1,faults.LostResponses);Assert.Equal(1,faults.FailedReads);
            await using var verify=data.CreateDbContext();Assert.Equal(EstadoEnvio.EnDeposito,(await verify.Envios.SingleAsync(e=>e.Id==shipment)).Estado);
            Assert.Equal(3,await verify.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM public.\"RecepcionesBulto\" WHERE \"EnvioId\"={shipment}").SingleAsync());
            Assert.All(await verify.Bultos.Where(b=>b.EnvioId==shipment).ToArrayAsync(),b=>{Assert.Equal(500,b.PesoGramos);Assert.Equal(EstadoCustodiaBulto.Deposito,b.Custodia);});
        }
        finally
        {
            if(!backoffice.HasExited)backoffice.Kill(true);await backoffice.WaitForExitAsync();
            await File.WriteAllTextAsync(Path.Combine(output,"backoffice-private.log"),await stdout+await stderr);
        }
    }
    // Test-only transport fault, after the real restricted API has committed its transaction.
    // Never registered by the application or exposed outside this ephemeral Kestrel fixture.
    private sealed class ReceiptFaults { public int LoseWrite; public int FailRead; public int LostResponses; public int FailedReads; }
    private sealed class ReceiptFaultFilter(ReceiptFaults faults):IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)=>app=>
        {
            app.Use(async(context,continuation)=>
            {
                var path=context.Request.Path.Value??"";
                if(path=="/__cu10_test/fail-receipt-response" && context.Request.Method=="POST")
                {Interlocked.Exchange(ref faults.LoseWrite,1);context.Response.StatusCode=204;return;}
                if(path=="/api/deposito/consulta" && Interlocked.Exchange(ref faults.FailRead,0)==1)
                {context.Response.StatusCode=503;await context.Response.WriteAsJsonAsync(new{detail="No pudimos recuperar el estado en esta prueba aislada."});Interlocked.Increment(ref faults.FailedReads);return;}
                if(path.StartsWith("/api/deposito/bultos/",StringComparison.Ordinal) && context.Request.Method=="POST" && Interlocked.Exchange(ref faults.LoseWrite,0)==1)
                {
                    var original=context.Response.Body;using var buffer=new MemoryStream();context.Response.Body=buffer;
                    try{await continuation();}finally{context.Response.Body=original;}
                    if(context.Response.StatusCode==200)
                    {
                        Interlocked.Exchange(ref faults.FailRead,1);context.Response.Clear();context.Response.StatusCode=503;
                        await context.Response.WriteAsJsonAsync(new{detail="No pudimos confirmar la respuesta. Reintentá el mismo cotejo."});
                        Interlocked.Increment(ref faults.LostResponses);
                    }
                    else{buffer.Position=0;await buffer.CopyToAsync(original);}
                    return;
                }
                await continuation();
            });
            next(app);
        };
    }
    private sealed class ReceiptBrowserFact:FactAttribute
    {public ReceiptBrowserFact(){if(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NEXORUTA_RECEIPT_E2E_DIR")))Skip="Opt-in real PDF/Chromium/checkout/API/PostgreSQL test.";}}
}
