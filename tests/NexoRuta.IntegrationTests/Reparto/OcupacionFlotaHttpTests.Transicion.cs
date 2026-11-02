using Npgsql;
using NexoRuta.Infrastructure.Persistence;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.IntegrationTests.Fixtures;
using static NexoRuta.IntegrationTests.Fixtures.RepartoHttpData;
namespace NexoRuta.IntegrationTests.Reparto;
public sealed partial class OcupacionFlotaHttpTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyTransitionPreservesUnknownFactsDetectsConflictsAndRequiresExplicitReconciliation(bool managedMigrator)
    {
        var data = await postgres.CreateDatabaseAsync();
        Preparado p; HojaRutaResponse a; HojaRutaResponse b;
        using (var factory = data.CreateApiFactory())
        using (var client = factory.CreateHttpClient())
        {
            p = await Preparar(data, client, verificarFlota: false);
            a = await Plan(client, p, FechaOperacion);
            b = await Plan(client, await Shipment(data, client, p), FechaOperacion.AddDays(1));
        }
        await using (var db = data.CreateDbContext())
        {
            foreach (var h in await db.HojasRuta.Include(h => h.Paradas).ToArrayAsync()) h.Despachar(h.Version);
            await db.SaveChangesAsync();
            // Bound this legacy fixture to B2 SQL. Later additive schemas must not be rolled back or their snapshots discarded.
            await FleetMigrationRange(db, "20261101185119_EnforceFleetOccupationExclusivity", "20261030120000_ListImportedShipmentReferences");
            await FleetMigrationRange(db, "20261030120000_ListImportedShipmentReferences", "20261101184716_StageFleetOccupations", managedMigrator);
        }
        await using (var db = data.CreateDbContext())
        {
            var unknown = await db.OcupacionesRuta.ToArrayAsync();
            Assert.Equal(2, unknown.Length);
            Assert.All(unknown, o => { Assert.True(o.Legada); Assert.Null(o.AdquiridaEnUtc); Assert.Null(o.AdquiridaPorAccesoId); Assert.Null(o.RetornoEnUtc); });
            Assert.Null((await db.Vehiculos.SingleAsync()).VerificacionInicialEnUtc);
            var conflict = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => FleetMigrationRange(db, "20261101184716_StageFleetOccupations", "20261101185119_EnforceFleetOccupationExclusivity", managedMigrator));
            Assert.Contains("conflict", conflict.MessageText, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, await db.OcupacionesRuta.CountAsync()); Assert.Empty(await db.HechosFlota.ToArrayAsync());
        }
        using (var factory = data.CreateApiFactory())
        using (var client = factory.CreateHttpClient())
        {
            await client.SetBearerForAccessAsync(data, p.AccesoId);
            var fleet = Assert.Single((await client.GetFromJsonAsync<VehiculoFlotaResponse[]>("/api/reparto/flota"))!);
            var oa = fleet.Ocupaciones.Single(o => o.RutaId == a.Id); var ob = fleet.Ocupaciones.Single(o => o.RutaId == b.Id);
            using var rejected = await client.PostAsJsonAsync($"/api/reparto/flota/{p.VehiculoId}/conciliar",
                new ComandoFlotaRequest(Guid.NewGuid(), oa.Id, oa.Version, "Inspección: dos autorizaciones incompatibles."));
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
            await Confirm(client, p.VehiculoId, "anular", new(Guid.NewGuid(), ob.Id, ob.Version, "Autorización futura nunca iniciada; se conserva historial."));
            await Confirm(client, p.VehiculoId, "conciliar", new(Guid.NewGuid(), oa.Id, oa.Version, "Inspección actual: ocupación vigente de esta ruta."));
        }
        await using (var db = data.CreateDbContext())
        {
            await FleetMigrationRange(db, "20261101184716_StageFleetOccupations", "20261101185119_EnforceFleetOccupationExclusivity", managedMigrator);
            var active = await db.OcupacionesRuta.SingleAsync(o => o.AnuladaEnUtc == null);
            Assert.NotNull(active.AdquiridaEnUtc); Assert.Null(active.RetornoEnUtc);
            Assert.Equal(2, await db.HechosFlota.CountAsync());
        }
    }
    private static async Task FleetMigrationRange(NexoRutaDbContext db, string from, string to, bool managed = false)
    {
        var script = db.GetService<IMigrator>().GenerateScript(from, to);
        if (!managed) { await db.Database.ExecuteSqlRawAsync(script); return; }
        // Dedicated ephemeral identity: owner privileges, no superuser, no BYPASSRLS or runtime membership.
        var role="fleet_migrator_"+Guid.NewGuid().ToString("N");
        var password=Guid.NewGuid().ToString("N");
        await db.Database.ExecuteSqlRawAsync(
            $"CREATE ROLE {role} LOGIN INHERIT CREATEROLE NOSUPERUSER NOCREATEDB NOREPLICATION NOBYPASSRLS PASSWORD '{password}'; " +
            $"GRANT nexoruta_schema_owner TO {role} WITH INHERIT TRUE, SET TRUE; " +
            $"GRANT USAGE,CREATE ON SCHEMA public TO {role}; " +
            $"GRANT SELECT,INSERT,UPDATE,DELETE ON public.\"__EFMigrationsHistory\" TO {role};");
        var cs=new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString()) { Username=role,Password=password,Pooling=false };
        await using var migration=new NexoRutaDbContext(new DbContextOptionsBuilder<NexoRutaDbContext>()
            .UseNexoRutaPostgres(cs.ConnectionString).Options);
        Assert.False(await migration.Database.SqlQueryRaw<bool>(
            "SELECT rolsuper OR rolbypassrls AS \"Value\" FROM pg_roles WHERE rolname=current_user").SingleAsync());
        await migration.Database.ExecuteSqlRawAsync(script);
        Assert.Equal(0,await db.Database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM pg_policies WHERE policyname LIKE 'migration_fleet_%'").SingleAsync());
    }
}
