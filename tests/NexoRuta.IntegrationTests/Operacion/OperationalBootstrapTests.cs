using Microsoft.EntityFrameworkCore;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguimiento;
namespace NexoRuta.IntegrationTests.Operacion;

[Collection(OperationalInfrastructureCollection.Name)]
public sealed class OperationalBootstrapTests(PostgresFixture postgres):IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task PopulatedUpgradeBuildsReplaySourcesWithoutChangingCanonicalFactsOrSendingOldNotices()
    {
        var data=await postgres.CreateDatabaseAsync("20261105180000_IndependentParcelProofDeadlines");
        Guid shipment;
        await using(var seed=data.CreateDbContext())shipment=(await SeguimientoTestData.CreateEnvioAsync(seed)).Id;
        await using var db=data.CreateDbContext();
        var before=await db.EnvioEventos.Where(x=>x.EnvioId==shipment).AsNoTracking().ToArrayAsync();
        var state=(await db.Envios.SingleAsync(x=>x.Id==shipment)).Estado;
        await db.Database.MigrateAsync();
        var after=await db.EnvioEventos.Where(x=>x.EnvioId==shipment).AsNoTracking().ToArrayAsync();
        Assert.Equal(before.Select(x=>x.Id),after.Select(x=>x.Id));
        Assert.Equal(state,(await db.Envios.AsNoTracking().SingleAsync(x=>x.Id==shipment)).Estado);
        Assert.True(await db.OutboxMessages.AnyAsync(x=>x.Tipo=="EventoOperativo"));
        Assert.Equal(0,await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM public.\"AvisosDestinatarioEstado\"").SingleAsync());
        Assert.Equal(0,await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_policies WHERE policyname='operational_bootstrap_read'").SingleAsync());
    }
}
