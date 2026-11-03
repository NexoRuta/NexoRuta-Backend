using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Reparto.Dtos;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Envios;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;
using NexoRuta.Infrastructure.Administracion;
using Npgsql;
using static NexoRuta.IntegrationTests.Fixtures.RepartoHttpData;
namespace NexoRuta.IntegrationTests.Reparto;
public sealed class RecepcionDepositoHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task MultiParcelReceiptIsIncompleteUntilAllAreComparedAndDoesNotRewriteDeclarationsOrTariff()
    {
        using var s=await PrepararEscenario(2);var b=s.Bultos[0];
        var before=await Declaracion(s);
        var consult=await s.Client.GetFromJsonAsync<ConsultaRecepcionDatos>($"/api/deposito/consulta?codigo={b.Id}");
        Assert.Equal(b.Id,consult!.BultoSeleccionadoId);Assert.Equal(2,consult.Bultos.Count);
        using var partial=await s.Client.PostAsJsonAsync(Url(b),Cotejo(b));
        Assert.Equal(HttpStatusCode.OK,partial.StatusCode);
        var result=(await partial.Content.ReadFromJsonAsync<ResultadoRecepcionDatos>())!;
        Assert.Equal("Admitido",result.Recepcion.EstadoEnvio);
        Assert.Single(result.Recepcion.Bultos,b=>b.Custodia=="Deposito");
        using var bypass=await s.Client.PostAsJsonAsync($"/api/deposito/envios/{b.EnvioId}/recepcion",new {Version=0});
        Assert.Equal(HttpStatusCode.Conflict,bypass.StatusCode);
        using var last=await s.Client.PostAsJsonAsync(Url(s.Bultos[1]),Cotejo(s.Bultos[1]));
        Assert.Equal(HttpStatusCode.OK,last.StatusCode);
        var complete=(await last.Content.ReadFromJsonAsync<ResultadoRecepcionDatos>())!;
        Assert.Equal("EnDeposito",complete.Recepcion.EstadoEnvio);Assert.All(complete.Recepcion.Bultos,x=>Assert.Equal("Conforme",x.CotejoActual));
        Assert.Equal(before,await Declaracion(s));
        await using var db=s.Data.CreateDbContext();
        Assert.Equal(2,await Count(db));
        var history=await db.EnvioEventos.SingleAsync(e=>e.EnvioId==b.EnvioId && e.EstadoNuevo=="EnDeposito");
        Assert.Equal("RecepcionDeposito",history.OrigenTipo);Assert.Equal(s.Acceso,history.ActorAccesoId);
        Assert.Equal(1,history.VersionNueva);
        foreach(var sql in new[]{"UPDATE public.\"RecepcionesBulto\" SET \"RegistradaEnUtc\"=clock_timestamp()","DELETE FROM public.\"RecepcionesBulto\"","TRUNCATE public.\"RecepcionesBulto\""})
        { var e=await Assert.ThrowsAsync<PostgresException>(()=>db.Database.ExecuteSqlRawAsync(sql));Assert.Equal("55000",e.SqlState); }
    }
    [Fact]
    public async Task DiscrepancyKeepsPhysicalCustodyAndPendingStateUntilANewConformingRecheckWithoutErasingHistory()
    {
        using var s=await PrepararEscenario(1);var b=s.Bultos[0];var before=await Declaracion(s);
        var difference=Cotejo(b) with {Conforme=false,PesoObservado=700,Observaciones="Peso distinto y embalaje dañado"};
        using var first=await s.Client.PostAsJsonAsync(Url(b),difference);Assert.Equal(HttpStatusCode.OK,first.StatusCode);
        var pending=(await first.Content.ReadFromJsonAsync<ResultadoRecepcionDatos>())!.Recepcion;
        Assert.Equal("Admitido",pending.EstadoEnvio);Assert.Equal("Deposito",pending.Bultos.Single().Custodia);
        Assert.Equal("Discrepancia pendiente",pending.Bultos.Single().CotejoActual);
        using var recheck=await s.Client.PostAsJsonAsync(Url(b),Cotejo(b) with {VersionBulto=pending.Bultos.Single().Version});
        Assert.Equal(HttpStatusCode.OK,recheck.StatusCode);
        var final=(await recheck.Content.ReadFromJsonAsync<ResultadoRecepcionDatos>())!.Recepcion;
        Assert.Equal("EnDeposito",final.EstadoEnvio);Assert.Equal(2,final.Historial.Count);
        Assert.True(final.Historial[0].Cotejo.Conforme);Assert.False(final.Historial[1].Cotejo.Conforme);
        Assert.Equal(700,final.Historial[1].Cotejo.PesoObservado);Assert.Equal(before,await Declaracion(s));
    }
    [Fact]
    public async Task RetriesAreScopedToActorPayloadAndParcelAndNewScansDoNotDuplicateFacts()
    {
        using var s=await PrepararEscenario(1);var b=s.Bultos[0];var command=Cotejo(b);
        using var first=await s.Client.PostAsJsonAsync(Url(b),command);Assert.Equal(HttpStatusCode.OK,first.StatusCode);
        using var retry=await s.Client.PostAsJsonAsync(Url(b),command);Assert.True((await retry.Content.ReadFromJsonAsync<ResultadoRecepcionDatos>())!.Repetida);
        using var changed=await s.Client.PostAsJsonAsync(Url(b),command with {Observaciones="Otro contenido"});Assert.Equal(HttpStatusCode.Conflict,changed.StatusCode);
        using var scan=await s.Client.PostAsJsonAsync(Url(b),command with {ComandoId=Guid.NewGuid(),VersionEnvio=1,VersionBulto=1});
        Assert.Equal(HttpStatusCode.OK,scan.StatusCode);Assert.True((await scan.Content.ReadFromJsonAsync<ResultadoRecepcionDatos>())!.Repetida);
        await using var db=s.Data.CreateDbContext();Assert.Equal(1,await Count(db));
        Assert.Single(await db.EnvioEventos.Where(e=>e.EnvioId==b.EnvioId&&e.EstadoNuevo=="EnDeposito").ToArrayAsync());
    }
    [Fact]
    public async Task UnknownForeignAndUnauthorizedParcelsAreNotExposed()
    {
        using var s=await PrepararEscenario(1);var b=s.Bultos[0];Guid foreign;
        await using(var db=s.Data.CreateDbContext())
        {
            var init=new AccesoInicial("foreign-receipt@example.test","Other receipt operator","Other commerce","foreign-depot@example.test");
            await AuthenticationTestData.Seeder(db,init).SeedAsync();
            var actor=await db.AccesosUsuario.SingleAsync(a=>a.OperadorId==init.OperadorId);
            var comercio=await db.AccesosUsuario.SingleAsync(a=>a.ComercioId==init.ComercioId);
            var otherOp=init.OperadorId!.Value;var otherCommerce=init.ComercioId!.Value;
            var version=await ServiciosEnvioTestData.PublicarReglasYServicioAsync(db,otherOp,actor.UsuarioId);
            var d=new Direccion(otherOp,"Synthetic other address");var p=new Destinatario(otherOp,"Other recipient","other@example.test");
            var e=new Envio(otherOp,otherCommerce,comercio.UsuarioId,p.Id,d.Id,version);
            var parcel=new Bulto(otherOp,e.Id,"FOREIGN-CODE",1,1,1,1);db.AddRange(d,p,e,parcel);await db.SaveChangesAsync();foreign=parcel.Id;
        }
        foreach(var id in new[]{Guid.NewGuid(),foreign})
        {
            using var get=await s.Client.GetAsync($"/api/deposito/consulta?codigo={id}");Assert.Equal(HttpStatusCode.NotFound,get.StatusCode);
            using var post=await s.Client.PostAsJsonAsync($"/api/deposito/bultos/{id}/recepcion",Cotejo(b));Assert.Equal(HttpStatusCode.NotFound,post.StatusCode);
        }
        using(var code=await s.Client.GetAsync("/api/deposito/consulta?codigo=FOREIGN-CODE"))Assert.Equal(HttpStatusCode.NotFound,code.StatusCode);
        using var unauth=s.Factory.CreateHttpClient();using var denied=await unauth.GetAsync($"/api/deposito/consulta?codigo={b.Id}");Assert.Equal(HttpStatusCode.Unauthorized,denied.StatusCode);
        await using var owner=s.Data.CreateDbContext();var commerce=await owner.AccesosUsuario.SingleAsync(a=>a.ComercioId==s.Comercio);
        await s.Client.SetBearerForAccessAsync(s.Data,commerce.Id);
        using var forbidden=await s.Client.GetAsync($"/api/deposito/consulta?codigo={b.Id}");Assert.Equal(HttpStatusCode.Forbidden,forbidden.StatusCode);
        using var forbiddenPost=await s.Client.PostAsJsonAsync(Url(b),Cotejo(b));Assert.Equal(HttpStatusCode.Forbidden,forbiddenPost.StatusCode);
        Assert.Equal(0,await Count(owner));
    }
    [Fact]
    public async Task StaleVersionsInvalidMeasuresAndIncompatibleStatesCannotReceive()
    {
        using var s=await PrepararEscenario(1);var b=s.Bultos[0];
        foreach(var command in new[]{Cotejo(b) with{VersionBulto=12},Cotejo(b) with{VersionEnvio=12}})
        { using var r=await s.Client.PostAsJsonAsync(Url(b),command);Assert.Equal(HttpStatusCode.Conflict,r.StatusCode); }
        foreach(var command in new[]{Cotejo(b) with{PesoObservado=999},Cotejo(b) with{PesoObservado=1.001m},Cotejo(b) with{Conforme=false,Observaciones=""}})
        { using var r=await s.Client.PostAsJsonAsync(Url(b),command);Assert.Equal(HttpStatusCode.BadRequest,r.StatusCode); }
        using var cancellation=await s.Client.PostAsJsonAsync($"/api/envios/{b.EnvioId}/cancelacion",new{Version=0,Motivo="Synthetic receipt cancellation"});Assert.Equal(HttpStatusCode.OK,cancellation.StatusCode);
        using var receipt=await s.Client.PostAsJsonAsync(Url(b),Cotejo(b) with{VersionEnvio=1});Assert.Equal(HttpStatusCode.Conflict,receipt.StatusCode);
        await using var db=s.Data.CreateDbContext();Assert.Equal(0,await Count(db));Assert.Equal(EstadoCustodiaBulto.SinRecepcion,(await db.Bultos.SingleAsync(x=>x.Id==b.Id)).Custodia);
    }
    [Fact]
    public async Task FailureOfReceiptOrCanonicalHistoryRollsBackCustodyStateAndAllEventsAndCanRetry()
    {
        using var s=await PrepararEscenario(1);var b=s.Bultos[0];var command=Cotejo(b);
        await using var db=s.Data.CreateDbContext();
        foreach(var table in new[]{"RecepcionesBulto","EnvioEventos"})
        {
            var add=table=="RecepcionesBulto"
                ? "ALTER TABLE public.\"RecepcionesBulto\" ADD CONSTRAINT receipt_test_reject CHECK (false)"
                : "ALTER TABLE public.\"EnvioEventos\" ADD CONSTRAINT receipt_test_reject CHECK (\"Tipo\" <> 'TransicionEstado')";
            await db.Database.ExecuteSqlRawAsync(add);
            var error=await Assert.ThrowsAnyAsync<Exception>(()=>s.Client.PostAsJsonAsync(Url(b),command));Assert.Contains("receipt_test_reject",error.ToString());
            Assert.Equal(0,await Count(db));
            var parcel=await db.Bultos.AsNoTracking().SingleAsync(x=>x.Id==b.Id);Assert.Equal(0,parcel.Version);Assert.Equal(EstadoCustodiaBulto.SinRecepcion,parcel.Custodia);
            Assert.Equal(EstadoEnvio.Admitido,(await db.Envios.AsNoTracking().SingleAsync(e=>e.Id==b.EnvioId)).Estado);
            var drop=table=="RecepcionesBulto" ? "ALTER TABLE public.\"RecepcionesBulto\" DROP CONSTRAINT receipt_test_reject" : "ALTER TABLE public.\"EnvioEventos\" DROP CONSTRAINT receipt_test_reject";
            await db.Database.ExecuteSqlRawAsync(drop);
        }
        using var retry=await s.Client.PostAsJsonAsync(Url(b),command);Assert.Equal(HttpStatusCode.OK,retry.StatusCode);Assert.Equal(1,await Count(db));
    }
    [Fact]
    public async Task IndependentOperatorsCompeteInPostgresAndOnlyOneReceiptAndTransitionAreWritten()
    {
        using var s=await PrepararEscenario(1);var b=s.Bultos[0];Guid secondAccess;
        await using(var db=s.Data.CreateDbContext())
        {
            var first=await db.AccesosUsuario.SingleAsync(a=>a.Id==s.Acceso);var role=await db.Roles.SingleAsync(r=>r.Id==first.RolId);
            var user=new Usuario("second-receipt@example.test",AuthenticationTestData.Hash("second-receipt@example.test"));
            var access=new AccesoUsuario(user.Id,first.OperadorId,null,role);db.AddRange(user,access);await db.SaveChangesAsync();secondAccess=access.Id;
        }
        using var other=s.Factory.CreateHttpClient();await other.SetBearerForAccessAsync(s.Data,secondAccess);
        await using var gate=new NpgsqlConnection(s.Data.ConnectionString);await gate.OpenAsync();await using var transaction=await gate.BeginTransactionAsync();
        await using(var lockCommand=new NpgsqlCommand("SELECT 1 FROM public.\"Operadores\" WHERE \"Id\"=@id FOR UPDATE",gate,transaction))
        {lockCommand.Parameters.AddWithValue("id",b.OperadorId);await lockCommand.ExecuteNonQueryAsync();}
        var firstTask=s.Client.PostAsJsonAsync(Url(b),Cotejo(b));var secondTask=other.PostAsJsonAsync(Url(b),Cotejo(b));
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));var waiting=0;
        while(waiting<2)
        {
            await using var probe=new NpgsqlCommand("SELECT count(*)::int FROM pg_stat_activity WHERE datname=current_database() AND pid<>pg_backend_pid() AND wait_event_type='Lock' AND query LIKE '%Operadores%FOR UPDATE%'",gate,transaction);
            waiting=(int)(await probe.ExecuteScalarAsync(timeout.Token))!;
            await using var refresh=new NpgsqlCommand("SELECT pg_stat_clear_snapshot()",gate,transaction);await refresh.ExecuteNonQueryAsync(timeout.Token);
            if(waiting<2)await Task.Delay(20,timeout.Token);
        }
        await transaction.CommitAsync();var responses=await Task.WhenAll(firstTask,secondTask);
        Assert.All(responses,r=>Assert.Equal(HttpStatusCode.OK,r.StatusCode));
        var results=await Task.WhenAll(responses.Select(r=>r.Content.ReadFromJsonAsync<ResultadoRecepcionDatos>()));Assert.Single(results,r=>r!.Repetida);
        foreach(var r in responses)r.Dispose();await using var verify=s.Data.CreateDbContext();Assert.Equal(1,await Count(verify));
        Assert.Single(await verify.EnvioEventos.Where(e=>e.EnvioId==b.EnvioId&&e.EstadoNuevo=="EnDeposito").ToArrayAsync());
    }
    [Fact]
    public async Task WarehouseRoleNeedsOnlyShipmentManagementAndOperatorWithoutItIsForbidden()
    {
        using var s=await PrepararEscenario(1);var b=s.Bultos[0];Guid operative,readOnly;
        await using(var db=s.Data.CreateDbContext())
        {
            var permission=await db.Permisos.SingleAsync(p=>p.Codigo=="envios.gestionar");
            var role=new Rol("warehouse-receipt","Warehouse receipt",b.OperadorId,null);
            var deniedRole=new Rol("warehouse-no-permission","No receipt permission",b.OperadorId,null);
            var user=new Usuario("warehouse-receipt@example.test",AuthenticationTestData.Hash("warehouse-receipt@example.test"));
            var deniedUser=new Usuario("warehouse-denied@example.test",AuthenticationTestData.Hash("warehouse-denied@example.test"));
            var access=new AccesoUsuario(user.Id,b.OperadorId,null,role);var deniedAccess=new AccesoUsuario(deniedUser.Id,b.OperadorId,null,deniedRole);
            db.AddRange(role,deniedRole,user,deniedUser,access,deniedAccess,new RolPermiso(role.Id,permission.Id));await db.SaveChangesAsync();operative=access.Id;readOnly=deniedAccess.Id;
        }
        await s.Client.SetBearerForAccessAsync(s.Data,readOnly);
        using(var query=await s.Client.GetAsync($"/api/deposito/consulta?codigo={b.Id}"))Assert.Equal(HttpStatusCode.Forbidden,query.StatusCode);
        using(var post=await s.Client.PostAsJsonAsync(Url(b),Cotejo(b)))Assert.Equal(HttpStatusCode.Forbidden,post.StatusCode);
        await s.Client.SetBearerForAccessAsync(s.Data,operative);
        using(var query=await s.Client.GetAsync($"/api/deposito/consulta?codigo={b.Id}"))Assert.Equal(HttpStatusCode.OK,query.StatusCode);
        using(var post=await s.Client.PostAsJsonAsync(Url(b),Cotejo(b)))Assert.Equal(HttpStatusCode.OK,post.StatusCode);
    }
    [Fact]
    public async Task AmbiguousPrintedCodeRequiresUuidAndOpenApiPublishesReceiptContracts()
    {
        using var s=await PrepararEscenario(1);var b=s.Bultos[0];
        await using(var db=s.Data.CreateDbContext())
        {
            var original=await db.Envios.SingleAsync(e=>e.Id==b.EnvioId);
            var other=new Envio(original.OperadorId,original.ComercioId,original.CreadoPorUsuarioId,original.DestinatarioId,original.DireccionId,original.VersionReglasId);
            db.Add(other);db.Add(new Bulto(b.OperadorId,other.Id,b.Codigo,1,1,1,1));await db.SaveChangesAsync();
        }
        using(var ambiguous=await s.Client.GetAsync("/api/deposito/consulta?codigo="+Uri.EscapeDataString(b.Codigo)))
        {Assert.Equal(HttpStatusCode.Conflict,ambiguous.StatusCode);Assert.Contains("UUID",await ambiguous.Content.ReadAsStringAsync());}
        using(var uuid=await s.Client.GetAsync($"/api/deposito/consulta?codigo={b.Id}"))Assert.Equal(HttpStatusCode.OK,uuid.StatusCode);
        using var development=new IntegrationApiFactory(s.Data,environment:"Development");using var client=development.CreateHttpClient();
        var schema=(await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json")).GetProperty("paths");
        Assert.True(schema.TryGetProperty("/api/deposito/consulta",out var queryPath));
        Assert.True(queryPath.GetProperty("get").GetProperty("responses").TryGetProperty("200",out _));
        Assert.True(schema.GetProperty("/api/deposito/bultos/{id}/recepcion").GetProperty("post").GetProperty("responses").TryGetProperty("200",out _));
    }

    private static string Url(Bulto b)=>$"/api/deposito/bultos/{b.Id}/recepcion";
    private static CotejoBultoDatos Cotejo(Bulto b)=>new(Guid.NewGuid(),0,0,true,b.PesoGramos,b.LargoCentimetros,b.AnchoCentimetros,b.AltoCentimetros,"");
    private static Task<int> Count(NexoRuta.Infrastructure.Persistence.NexoRutaDbContext db)=>db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM public.\"RecepcionesBulto\"").SingleAsync();
    private static async Task<string> Declaracion(Scene s)
    {
        await using var db=s.Data.CreateDbContext();var ids=s.Bultos.Select(b=>b.Id).ToArray();
        var bultos=await db.Bultos.AsNoTracking().Where(b=>ids.Contains(b.Id)).OrderBy(b=>b.Id).Select(b=>new{b.Id,b.Codigo,b.PesoGramos,b.LargoCentimetros,b.AnchoCentimetros,b.AltoCentimetros}).ToArrayAsync();
        var envio=s.Bultos[0].EnvioId;var charge=await db.Database.SqlQuery<string>($"SELECT \"Resultado\"::text AS \"Value\" FROM public.\"TarifasEnvio\" WHERE \"EnvioId\"={envio}").SingleAsync();
        return JsonSerializer.Serialize(new{bultos,charge});
    }
    private async Task<Scene> PrepararEscenario(int cantidad)
    {
        var data=await postgres.CreateDatabaseAsync();Guid access,commerce,shipment;
        using(var setup=data.CreateShippingApiFactory())using(var client=setup.CreateHttpClient())
        {
            await using var db=data.CreateDbContext();var actor=await db.AccesosUsuario.SingleAsync(a=>a.OperadorId!=null);var c=await db.AccesosUsuario.SingleAsync(a=>a.ComercioId!=null);
            access=actor.Id;commerce=c.ComercioId!.Value;await client.SetBearerForAccessAsync(data,c.Id);
            using var created=await client.PostAsJsonAsync("/api/envios",new{OperadorId=actor.OperadorId,Servicio=ServiciosEnvioTestData.Declarar(actor.OperadorId),DestinatarioNombre="Synthetic receipt recipient",DestinatarioEmail="receipt@example.test",Direccion="Synthetic receipt address",Bultos=Enumerable.Range(1,cantidad).Select(i=>new{Codigo="RECEIPT-"+i,PesoGramos=500,LargoCentimetros=20,AnchoCentimetros=10,AltoCentimetros=5}).ToArray()});
            Assert.Equal(HttpStatusCode.Created,created.StatusCode);shipment=(await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        }
        await using var owner=data.CreateDbContext();var parcels=await owner.Bultos.AsNoTracking().Where(b=>b.EnvioId==shipment).OrderBy(b=>b.Codigo).ToArrayAsync();
        var restricted=await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);var factory=new IntegrationApiFactory(restricted,settings:RlsRestrictedHttpFlowTests.ApiSettings());var api=factory.CreateHttpClient();await api.SetBearerForAccessAsync(data,access);
        return new(data,factory,api,parcels,access,commerce);
    }
    private sealed record Scene(IntegrationDatabase Data,IntegrationApiFactory Factory,HttpClient Client,Bulto[] Bultos,Guid Acceso,Guid Comercio):IDisposable
    {public void Dispose(){Client.Dispose();Factory.Dispose();}}
}
