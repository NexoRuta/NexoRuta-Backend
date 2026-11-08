using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Api.Features.Webhooks;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Administracion;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.Infrastructure.Webhooks;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;
using Npgsql;
namespace NexoRuta.IntegrationTests.Webhooks;
public sealed class WebhooksHttpTests(PostgresFixture postgres):IClassFixture<PostgresFixture>
{
    internal const string Receiver="http://127.0.0.1:5103/comercio/webhook";
    internal static readonly string Secret="whsec_"+Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    internal static Dictionary<string,string?> Settings(){var s=RlsRestrictedHttpFlowTests.ApiSettings();s["Webhooks:DevelopmentReceiverUrl"]=Receiver;return s;}
    [Fact] public async Task AtomicFanoutVersionsConfigurationSnapshotsAndOptimisticConcurrency()
    {
        var data=await postgres.CreateDatabaseAsync();using(var seed=data.CreateShippingApiFactory())using(seed.CreateHttpClient()){}
        var runtime=await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);using var factory=new IntegrationApiFactory(runtime,settings:Settings());using var client=factory.CreateHttpClient();
        await using var owner=data.CreateDbContext();var op=(await owner.Operadores.SingleAsync()).Id;var commerce=await owner.AccesosUsuario.SingleAsync(a=>a.ComercioId!=null);
        await client.SetBearerForAccessAsync(data,commerce.Id);
        var sub=await Subscribe(client,op,2,["EnvioCreado","BultoActualizado"]);var legacy=await Subscribe(client,op,1,["EnvioCreado"]);
        var shipment=await Create(client,op);
        var store=new WebhookStore(owner);var deliveries=await store.Deliveries.ToArrayAsync();Assert.Equal(2,deliveries.Length);
        var v2=Assert.Single(deliveries,d=>d.Contrato==2);var v1=Assert.Single(deliveries,d=>d.Contrato==1);
        Assert.Equal(v1.EventoId,v2.EventoId);Assert.Equal(shipment,v2.EnvioId);
        Assert.Equal(new[]{"comercioId","envioId","estado","operadorId"},JsonDocument.Parse(v1.Payload).RootElement.EnumerateObject().Select(x=>x.Name).Order());
        Assert.DoesNotContain("destinatario",v2.Payload,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("token",v2.Payload,StringComparison.OrdinalIgnoreCase);
        using(var update=await client.PutAsJsonAsync($"/api/webhooks/suscripciones/{sub.Id}",new SaveWebhook(op,Receiver, ["OperacionActualizada"],2,false,sub.Revision,Secret))) Assert.Equal(HttpStatusCode.OK,update.StatusCode);
        using(var conflict=await client.PutAsJsonAsync($"/api/webhooks/suscripciones/{sub.Id}",new SaveWebhook(op,Receiver,["EnvioCreado"],2,true,sub.Revision,null))) Assert.Equal(HttpStatusCode.Conflict,conflict.StatusCode);
        Assert.Equal(v2.Payload,(await store.Deliveries.SingleAsync(d=>d.Id==v2.Id)).Payload);Assert.Equal(1,(await store.Deliveries.SingleAsync(d=>d.Id==v2.Id)).Revision);
        using var listing=await client.GetAsync("/api/webhooks/suscripciones");var text=await listing.Content.ReadAsStringAsync();Assert.DoesNotContain(Secret,text);Assert.DoesNotContain("secretoProtegido",text,StringComparison.OrdinalIgnoreCase);
        await Create(client,op);Assert.Equal(3,await store.Deliveries.CountAsync()); // disabled subscription stops new events only
        await using(var tx=await owner.Database.BeginTransactionAsync())
        {
            await owner.Database.ExecuteSqlInterpolatedAsync($"""SELECT set_config('nexoruta.actor_kind','comercio',true),set_config('nexoruta.org_id',{commerce.ComercioId.ToString()},true),set_config('nexoruta.user_id',{commerce.UsuarioId.ToString()},true)""");
            await owner.Database.ExecuteSqlInterpolatedAsync($"""UPDATE public."WebhookSubscriptions" SET "Revision"="Revision"+1,"Habilitada"=true WHERE "Id"={sub.Id}""");
            await tx.RollbackAsync();
        }
        Assert.False((await store.Subscriptions.SingleAsync(s=>s.Id==sub.Id)).Habilitada);
        Guid rolledBack;
        await using(var tx=await owner.Database.BeginTransactionAsync())
        {
            var recipient=new NexoRuta.Domain.Envios.Destinatario(op,"Synthetic rollback","rollback@example.test");var address=new NexoRuta.Domain.Envios.Direccion(op,"Synthetic rollback 123");
            var shipmentRow=new NexoRuta.Domain.Envios.Envio(op,commerce.ComercioId!.Value,commerce.UsuarioId,recipient.Id,address.Id,(await owner.ReglasOperativasOperadores.SingleAsync()).VersionVigenteId!.Value);
            rolledBack=shipmentRow.Id;owner.AddRange(recipient,address,shipmentRow);await owner.SaveChangesAsync();
            await owner.Database.ExecuteSqlRawAsync("SET CONSTRAINTS webhook_capture IMMEDIATE");
            Assert.True(await store.Deliveries.AnyAsync(d=>d.EnvioId==rolledBack));
            await using var separate=data.CreateDbContext();Assert.False(await new WebhookStore(separate).Deliveries.AnyAsync(d=>d.EnvioId==rolledBack));
            await tx.RollbackAsync();
        }
        await using var final=data.CreateDbContext();Assert.False(await final.Envios.AnyAsync(e=>e.Id==rolledBack));Assert.False(await new WebhookStore(final).Deliveries.AnyAsync(d=>d.EnvioId==rolledBack));
    }
    [Fact] public async Task DurableClaimsCoordinateWorkersFenceExpiredLeasesExhaustAndAuditReplay()
    {
        var data=await postgres.CreateDatabaseAsync();using(var seed=data.CreateShippingApiFactory())using(seed.CreateHttpClient()){}
        var runtime=await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);using var factory=new IntegrationApiFactory(runtime,settings:Settings());using var client=factory.CreateHttpClient();
        await using var owner=data.CreateDbContext();var op=(await owner.Operadores.SingleAsync()).Id;var commerce=await owner.AccesosUsuario.SingleAsync(a=>a.ComercioId!=null);await client.SetBearerForAccessAsync(data,commerce.Id);
        await Subscribe(client,op,2,["EnvioCreado"]);await Create(client,op);
        var worker=await WorkerDatabase(data);await using var first=worker.CreateDbContext();await using var second=worker.CreateDbContext();
        var a=Guid.NewGuid();var b=Guid.NewGuid();var claims=await Task.WhenAll(new WebhookStore(first).Claim(a,2,default),new WebhookStore(second).Claim(b,2,default));Assert.Single(claims,x=>x is not null);
        var index=Array.FindIndex(claims,x=>x is not null);var token=index==0?a:b;var claimed=claims[index]!;
        await owner.Database.ExecuteSqlInterpolatedAsync($"""UPDATE public."WebhookDeliveries" SET "ReservaHastaUtc"=clock_timestamp()-interval '1 second' WHERE "Id"={claimed.Id}""");
        var next=await new WebhookStore(second).Claim(b=Guid.NewGuid(),2,default);Assert.NotNull(next);Assert.Equal(claimed.EventoId,next.EventoId);Assert.Equal(claimed.Payload,next.Payload);
        Assert.False(await new WebhookStore(first).Finish(claimed.Id,token,200,"late",false,1,default));
        Assert.True(await new WebhookStore(second).Finish(claimed.Id,b,503,"HTTP 503",true,1,default));
        Assert.Equal("Fallido",(await new WebhookStore(owner).Deliveries.SingleAsync()).Estado);
        using var replay=await client.PostAsJsonAsync($"/api/webhooks/entregas/{claimed.Id}/reenvios",new{});Assert.Equal(HttpStatusCode.OK,replay.StatusCode);
        var again=await new WebhookStore(first).Claim(Guid.NewGuid(),2,default);Assert.NotNull(again);Assert.Equal(claimed.EventoId,again.EventoId);
        var detail=await client.GetFromJsonAsync<JsonElement>($"/api/webhooks/entregas/{claimed.Id}");Assert.Equal(1,detail.GetProperty("reenvios").GetArrayLength());Assert.Equal(3,detail.GetProperty("intentos").GetArrayLength());
    }
    [Fact] public async Task RlsSeparatesTwoCommercesAndTwoOperatorsIncludingManipulatedIdsAndFilters()
    {
        var data=await postgres.CreateDatabaseAsync();
        await using var owner=data.CreateDbContext();
        foreach(var suffix in new[]{"a","b"}) await AuthenticationTestData.Seeder(owner,new AccesoInicial($"wh-{suffix}@example.test","Operator "+suffix,"Commerce "+suffix,$"wh-op-{suffix}@example.test")).SeedAsync();
        await ReglasEnvioTestData.SembrarInicialesAsync(owner);await ServiciosEnvioTestData.SembrarAsync(owner);
        var actors=await owner.AccesosUsuario.Where(x=>x.ComercioId!=null).OrderBy(x=>x.Id).ToArrayAsync();var ops=await owner.Operadores.OrderBy(x=>x.Id).ToArrayAsync();
        var runtime=await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);using var factory=new IntegrationApiFactory(runtime,settings:Settings(),seedOperationalRules:false);using var client=factory.CreateHttpClient();
        await client.SetBearerForAccessAsync(data,actors[0].Id);var mine=await Subscribe(client,ops[0].Id,2,["EnvioCreado"]);await Subscribe(client,ops[1].Id,2,["EnvioCreado"]);
        await Create(client,ops[0].Id);await Create(client,ops[1].Id);var delivery=await new WebhookStore(owner).Deliveries.FirstAsync(x=>x.OperadorId==ops[0].Id);
        await client.SetBearerForAccessAsync(data,actors[1].Id);
        Assert.Empty((await client.GetFromJsonAsync<WebhookSubscription[]>("/api/webhooks/suscripciones"))!);
        Assert.Equal(0,(await client.GetFromJsonAsync<JsonElement>($"/api/webhooks/entregas?operadorId={ops[0].Id}")).GetProperty("total").GetInt32());
        using var detail=await client.GetAsync($"/api/webhooks/entregas/{delivery.Id}");Assert.Equal(HttpStatusCode.NotFound,detail.StatusCode);
        using var replay=await client.PostAsJsonAsync($"/api/webhooks/entregas/{delivery.Id}/reenvios",new{});Assert.Equal(HttpStatusCode.NotFound,replay.StatusCode);
        using var overwrite=await client.PutAsJsonAsync($"/api/webhooks/suscripciones/{mine.Id}",new SaveWebhook(ops[0].Id,Receiver,["EnvioCreado"],2,true,mine.Revision,null));Assert.Equal(HttpStatusCode.NotFound,overwrite.StatusCode);
        var operatorAccess=await owner.AccesosUsuario.FirstAsync(x=>x.OperadorId==ops[1].Id);await client.SetBearerForAccessAsync(data,operatorAccess.Id);
        using var other=await client.GetAsync($"/api/webhooks/entregas/{delivery.Id}");Assert.Equal(HttpStatusCode.NotFound,other.StatusCode);
        Assert.Equal(1,(await client.GetFromJsonAsync<JsonElement>($"/api/webhooks/entregas?operadorId={ops[1].Id}")).GetProperty("total").GetInt32());
    }
    [Fact] public async Task RevocationCancelsHistoricalPendingAndRequiresNewKeyToEnable()
    {
        var data=await postgres.CreateDatabaseAsync();using(var seed=data.CreateShippingApiFactory())using(seed.CreateHttpClient()){}
        var runtime=await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);using var factory=new IntegrationApiFactory(runtime,settings:Settings());using var client=factory.CreateHttpClient();
        await using var owner=data.CreateDbContext();var op=(await owner.Operadores.SingleAsync()).Id;var actor=await owner.AccesosUsuario.SingleAsync(a=>a.ComercioId!=null);await client.SetBearerForAccessAsync(data,actor.Id);
        var subscription=await Subscribe(client,op,2,["EnvioCreado"]);await Create(client,op);
        using var revoke=await client.PostAsJsonAsync($"/api/webhooks/suscripciones/{subscription.Id}/revocaciones",subscription.Revision);Assert.Equal(HttpStatusCode.OK,revoke.StatusCode);
        var row=await new WebhookStore(owner).Subscriptions.SingleAsync();Assert.True(row.Revocada);Assert.False(row.Habilitada);
        var delivery=await new WebhookStore(owner).Deliveries.SingleAsync();Assert.Equal("Cancelado",delivery.Estado);
        using var replay=await client.PostAsJsonAsync($"/api/webhooks/entregas/{delivery.Id}/reenvios",new{});Assert.Equal(HttpStatusCode.Conflict,replay.StatusCode);
        using var staleKey=await client.PutAsJsonAsync($"/api/webhooks/suscripciones/{subscription.Id}",new SaveWebhook(op,Receiver,["EnvioCreado"],2,true,row.Revision,null));Assert.Equal(HttpStatusCode.BadRequest,staleKey.StatusCode);
        using var enabled=await client.PutAsJsonAsync($"/api/webhooks/suscripciones/{subscription.Id}",new SaveWebhook(op,Receiver,["EnvioCreado"],2,true,row.Revision,"whsec_"+Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));Assert.Equal(HttpStatusCode.OK,enabled.StatusCode);
        await Create(client,op);Assert.Equal(1,await new WebhookStore(owner).Deliveries.CountAsync(d=>d.Estado=="Pendiente"));
    }
    internal static async Task<WebhookSubscription> Subscribe(HttpClient client,Guid op,int version,string[] events)
    {
        using var response=await client.PutAsJsonAsync($"/api/webhooks/suscripciones/{Guid.NewGuid()}",new SaveWebhook(op,Receiver,events,version,true,0,Secret));Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());return (await response.Content.ReadFromJsonAsync<WebhookSubscription>())!;
    }
    internal static async Task<Guid> Create(HttpClient client,Guid op)
    {
        using var response=await client.PostAsJsonAsync("/api/envios",new{OperadorId=op,Servicio=ServiciosEnvioTestData.Declarar(op),DestinatarioNombre="Synthetic webhook recipient",DestinatarioEmail="webhook@example.test",Direccion="Synthetic 123",Bultos=new[]{new{Codigo="WH-"+Guid.NewGuid().ToString("N"),PesoGramos=100m,LargoCentimetros=10m,AnchoCentimetros=10m,AltoCentimetros=10m}}});Assert.True(response.IsSuccessStatusCode,await response.Content.ReadAsStringAsync());return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
    internal static async Task<IntegrationDatabase> WorkerDatabase(IntegrationDatabase data)
    {
        var role="wh_worker_"+Guid.NewGuid().ToString("N");var password=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));await using var db=new NpgsqlConnection(data.ConnectionString);await db.OpenAsync();await using var cmd=new NpgsqlCommand($"CREATE ROLE {role} LOGIN PASSWORD '{password}' NOSUPERUSER NOBYPASSRLS;GRANT nexoruta_worker TO {role}",db);await cmd.ExecuteNonQueryAsync();return new IntegrationDatabase(new NpgsqlConnectionStringBuilder(data.ConnectionString){Username=role,Password=password}.ConnectionString);
    }
}
