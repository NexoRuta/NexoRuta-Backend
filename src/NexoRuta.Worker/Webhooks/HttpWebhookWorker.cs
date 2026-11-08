using System.Globalization;
using System.Net;
using System.Text;
using NexoRuta.Infrastructure.Webhooks;
namespace NexoRuta.Worker.Webhooks;

public sealed class HttpWebhookWorker(IServiceScopeFactory scopes, IConfiguration configuration, WebhookDestinationPolicy policy, WebhookSecrets secrets, ILogger<HttpWebhookWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var max=configuration.GetValue("Webhooks:MaxAttempts",6);
        var timeout=configuration.GetValue("Webhooks:TimeoutSeconds",15);
        var basis=configuration.GetValue("Webhooks:RetryBaseSeconds",10);
        var cap=configuration.GetValue("Webhooks:RetryMaxSeconds",3600);
        if(max is <1 or >20 || timeout is <1 or >30 || basis<1 || cap<basis || cap>86400) throw new InvalidOperationException("Invalid webhook worker limits.");
        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope=scopes.CreateScope();
                var store=scope.ServiceProvider.GetRequiredService<WebhookStore>();
                var token=Guid.NewGuid();
                var d=await store.Claim(token,max,stoppingToken);
                WorkerTelemetry.SqlContact("HttpWebhooks");
                if(d is null) { await Task.Delay(500,stoppingToken); continue; }
                int? status=null; string? diagnostic=null; bool retry=true;
                var delay=(int)Math.Min(cap,basis*Math.Pow(2,Math.Min(20,d.IntentosCiclo-1)));
                try
                {
                    var url=policy.Validate(d.Url); // Revalidate old snapshots against current egress policy.
                    using var http=new HttpClient(policy.CreateHandler(url)) {Timeout=TimeSpan.FromSeconds(timeout)};
                    using var request=new HttpRequestMessage(HttpMethod.Post,url);
                    var bytes=Encoding.UTF8.GetBytes(d.Payload);
                    request.Content=new ByteArrayContent(bytes);
                    request.Content.Headers.ContentType=new("application/json");
                    var id=d.EventoId.ToString("D");
                    var timestamp=DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
                    request.Headers.Add("webhook-id",id);
                    request.Headers.Add("webhook-timestamp",timestamp);
                    request.Headers.Add("webhook-signature",WebhookSignature.Sign(secrets.Unprotect(d.SecretoProtegido),id,timestamp,bytes));
                    request.Headers.Add("NexoRuta-Contract-Version",d.Contrato.ToString(CultureInfo.InvariantCulture));
                    request.Headers.Add("NexoRuta-Event-Type",d.Tipo);
                    request.Headers.Add("NexoRuta-Key-Id",d.ClaveId.ToString("D"));
                    request.Headers.Add("NexoRuta-Delivery-Id",d.Id.ToString("D"));
                    using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,stoppingToken);
                    status=(int)response.StatusCode;
                    retry=status>=500 || status is 408 or 425 or 429;
                    diagnostic=response.IsSuccessStatusCode ? "Confirmado por receptor" : $"HTTP {status}";
                    var retryAfter=response.Headers.RetryAfter;
                    var wait=retryAfter?.Delta?.TotalSeconds ?? (retryAfter?.Date-DateTimeOffset.UtcNow)?.TotalSeconds;
                    if(wait>0) delay=(int)Math.Clamp(Math.Max(delay,wait.Value),1,cap);
                    // Error bodies are deliberately never persisted or logged.
                }
                catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { throw; }
                catch(OperationCanceledException) { diagnostic="Timeout: resultado remoto desconocido"; }
                catch(HttpRequestException) { diagnostic="Error de red: resultado remoto desconocido"; }
                catch(ArgumentException) { diagnostic="Destino no autorizado"; retry=false; }
                catch(System.Security.Cryptography.CryptographicException) { diagnostic="Clave de firma no disponible"; retry=false; }
                if(status is >=200 and <=299) WorkerTelemetry.Published("HttpWebhooks");
                else { WorkerTelemetry.Failed("HttpWebhooks"); WorkerTelemetry.CommerceNotificationFailed(); }
                var saved=await store.Finish(d.Id,token,status,diagnostic,retry,delay,stoppingToken);
                logger.LogInformation("Webhook attempt finished. DeliveryId: {DeliveryId}, HttpStatus: {HttpStatus}, LeaseConfirmed: {LeaseConfirmed}",d.Id,status,saved);
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { break; }
            catch(Exception ex)
            {
                logger.LogError("Webhook worker failure. ErrorType: {ErrorType}",ex.GetType().Name);
                await Task.Delay(1000,stoppingToken);
            }
        }
    }
}
