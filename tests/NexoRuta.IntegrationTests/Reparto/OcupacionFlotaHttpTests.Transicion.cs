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
    [Fact]
    public async Task LegacyTransitionPreservesUnknownFactsDetectsConflictsAndRequiresExplicitReconciliation()
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
            await FleetMigrationRange(db, "20261030120000_ListImportedShipmentReferences", "20261101184716_StageFleetOccupations");
        }
        await using (var db = data.CreateDbContext())
        {
            var unknown = await db.OcupacionesRuta.ToArrayAsync();
            Assert.Equal(2, unknown.Length);
            Assert.All(unknown, o => { Assert.True(o.Legada); Assert.Null(o.AdquiridaEnUtc); Assert.Null(o.AdquiridaPorAccesoId); Assert.Null(o.RetornoEnUtc); });
            Assert.Null((await db.Vehiculos.SingleAsync()).VerificacionInicialEnUtc);
            var conflict = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => FleetMigrationRange(db, "20261101184716_StageFleetOccupations", "20261101185119_EnforceFleetOccupationExclusivity"));
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
            await FleetMigrationRange(db, "20261101184716_StageFleetOccupations", "20261101185119_EnforceFleetOccupationExclusivity");
            var active = await db.OcupacionesRuta.SingleAsync(o => o.AnuladaEnUtc == null);
            Assert.NotNull(active.AdquiridaEnUtc); Assert.Null(active.RetornoEnUtc);
            Assert.Equal(2, await db.HechosFlota.CountAsync());
        }
    }
    private static Task FleetMigrationRange(NexoRuta.Infrastructure.Persistence.NexoRutaDbContext db, string from, string to) =>
        db.Database.ExecuteSqlRawAsync(db.GetService<IMigrator>().GenerateScript(from, to));
}
