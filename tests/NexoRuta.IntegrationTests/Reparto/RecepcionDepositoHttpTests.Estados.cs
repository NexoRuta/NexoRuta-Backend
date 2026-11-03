using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Domain.Envios;
using NexoRuta.IntegrationTests.Fixtures;
using Npgsql;
namespace NexoRuta.IntegrationTests.Reparto;

public sealed partial class RecepcionDepositoHttpTests
{
    [Fact]
    public async Task CompleteReceiptAndCommerceCancellationCompeteWithoutPartialStateCustodyOrEvents()
    {
        using var s=await PrepararEscenario(1);var parcel=s.Bultos[0];var receiptCommand=Cotejo(parcel);
        using var commerce=s.Factory.CreateHttpClient();
        await using(var db=s.Data.CreateDbContext())
        {
            var access=await db.AccesosUsuario.SingleAsync(a=>a.ComercioId==s.Comercio);
            await commerce.SetBearerForAccessAsync(s.Data,access.Id);
        }
        var cancelUrl=$"/api/envios/{parcel.EnvioId}/cancelacion";
        var cancelCommand=new{Version=0,Motivo="Synthetic incompatible operation"};
        await using var gate=new NpgsqlConnection(s.Data.ConnectionString);await gate.OpenAsync();
        await using var transaction=await gate.BeginTransactionAsync();
        await using(var block=new NpgsqlCommand("SELECT 1 FROM public.\"Operadores\" WHERE \"Id\"=@id FOR UPDATE",gate,transaction))
        {block.Parameters.AddWithValue("id",parcel.OperadorId);await block.ExecuteNonQueryAsync();}
        var receipt=s.Client.PostAsJsonAsync(Url(parcel),receiptCommand);
        var cancel=commerce.PostAsJsonAsync(cancelUrl,cancelCommand);
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var waiting=0;
        while(waiting<2)
        {
            await using(var refresh=new NpgsqlCommand("SELECT pg_stat_clear_snapshot()",gate,transaction))await refresh.ExecuteNonQueryAsync(deadline.Token);
            await using var probe=new NpgsqlCommand("""
                SELECT count(*)::int FROM pg_stat_activity
                WHERE datname=current_database() AND pid<>pg_backend_pid() AND wait_event_type='Lock'
                    AND (query LIKE '%Operadores%FOR UPDATE%' OR query LIKE '%cancel_shipment%')
                """,gate,transaction);
            waiting=(int)(await probe.ExecuteScalarAsync(deadline.Token))!;
            if(waiting<2)await Task.Delay(20,deadline.Token);
        }
        Assert.False(receipt.IsCompleted);Assert.False(cancel.IsCompleted);
        await transaction.CommitAsync();
        using var received=await receipt;using var cancelled=await cancel;
        Assert.Equal(new[]{HttpStatusCode.OK,HttpStatusCode.Conflict},new[]{received.StatusCode,cancelled.StatusCode}.OrderBy(c=>(int)c));
        var receiptWon=received.StatusCode==HttpStatusCode.OK;
        await using var verify=s.Data.CreateDbContext();
        var shipment=await verify.Envios.AsNoTracking().SingleAsync(e=>e.Id==parcel.EnvioId);
        Assert.Equal(receiptWon?EstadoEnvio.EnDeposito:EstadoEnvio.Cancelado,shipment.Estado);Assert.Equal(1,shipment.Version);
        var actualParcel=await verify.Bultos.AsNoTracking().SingleAsync(b=>b.Id==parcel.Id);
        Assert.Equal(receiptWon?EstadoCustodiaBulto.Deposito:EstadoCustodiaBulto.SinRecepcion,actualParcel.Custodia);
        Assert.Equal(receiptWon?1:0,actualParcel.Version);Assert.Equal(receiptWon?1:0,await Count(verify));
        Assert.Equal(receiptWon?0:1,await verify.CancelacionesEnvio.CountAsync(c=>c.EnvioId==parcel.EnvioId));
        var transition=Assert.Single(await verify.EnvioEventos.Where(e=>e.EnvioId==parcel.EnvioId&&e.Tipo=="TransicionEstado").ToArrayAsync());
        Assert.Equal("Admitido",transition.EstadoAnterior);Assert.Equal(shipment.Estado.ToString(),transition.EstadoNuevo);
        Assert.Equal(0,transition.VersionAnterior);Assert.Equal(1,transition.VersionNueva);
        using var receiptRetry=await s.Client.PostAsJsonAsync(Url(parcel),receiptCommand);
        using var cancelRetry=await commerce.PostAsJsonAsync(cancelUrl,cancelCommand);
        Assert.Equal(received.StatusCode,receiptRetry.StatusCode);Assert.Equal(cancelled.StatusCode,cancelRetry.StatusCode);
        Assert.Equal(1,await verify.EnvioEventos.CountAsync(e=>e.EnvioId==parcel.EnvioId&&e.Tipo=="TransicionEstado"));
        Assert.Equal(receiptWon?1:0,await Count(verify));
    }

    [Fact]
    public async Task FailurePersistingCommercialStateRollsBackReceiptCustodyAndHistoryThenExactRetrySucceeds()
    {
        using var s=await PrepararEscenario(1);var parcel=s.Bultos[0];var command=Cotejo(parcel);var declared=await Declaracion(s);
        await using var db=s.Data.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE public.\"Envios\" ADD CONSTRAINT cu11_reject_state CHECK (\"Estado\" <> 'EnDeposito')");
        try
        {
            var failure=await Assert.ThrowsAnyAsync<Exception>(()=>s.Client.PostAsJsonAsync(Url(parcel),command));
            Assert.Contains("cu11_reject_state",failure.ToString());
            var shipment=await db.Envios.AsNoTracking().SingleAsync(e=>e.Id==parcel.EnvioId);
            Assert.Equal(EstadoEnvio.Admitido,shipment.Estado);Assert.Equal(0,shipment.Version);
            var actual=await db.Bultos.AsNoTracking().SingleAsync(b=>b.Id==parcel.Id);
            Assert.Equal(EstadoCustodiaBulto.SinRecepcion,actual.Custodia);Assert.Equal(0,actual.Version);
            Assert.Equal(0,await Count(db));Assert.False(await db.EnvioEventos.AnyAsync(e=>e.EnvioId==parcel.EnvioId&&e.Tipo=="TransicionEstado"));
            Assert.Equal(declared,await Declaracion(s));
        }
        finally {await db.Database.ExecuteSqlRawAsync("ALTER TABLE public.\"Envios\" DROP CONSTRAINT cu11_reject_state");}
        using var retry=await s.Client.PostAsJsonAsync(Url(parcel),command);Assert.Equal(HttpStatusCode.OK,retry.StatusCode);
        Assert.Equal(1,await Count(db));Assert.Equal(1,await db.EnvioEventos.CountAsync(e=>e.EnvioId==parcel.EnvioId&&e.EstadoNuevo=="EnDeposito"));
    }
}
