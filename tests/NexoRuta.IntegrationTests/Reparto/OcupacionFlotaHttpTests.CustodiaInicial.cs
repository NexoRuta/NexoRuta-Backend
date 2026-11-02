using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.Domain.Envios;
using NexoRuta.IntegrationTests.Fixtures;
using static NexoRuta.IntegrationTests.Fixtures.RepartoHttpData;
namespace NexoRuta.IntegrationTests.Reparto;
public sealed partial class OcupacionFlotaHttpTests
{
    [Fact]
    public async Task HistoricalClosedRouteDoesNotInventReturnAndInitialReceiptIsExplicitAndAudited()
    {
        var data = await postgres.CreateDatabaseAsync(); Preparado p; HojaRutaResponse route;
        using (var factory = data.CreateApiFactory())
        using (var client = factory.CreateHttpClient())
        {
            p = await Preparar(data, client, verificarFlota: false); route = await Plan(client, p, FechaOperacion);
        }
        await using (var db = data.CreateDbContext())
        {
            var h = await db.HojasRuta.Include(h => h.Paradas).SingleAsync();
            h.Despachar(h.Version); h.RegistrarInicio(DateTimeOffset.UtcNow);
            var b = await db.Bultos.SingleAsync(); b.Cargar(p.AccesoId, h.Id); h.RegistrarFin(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
            await FleetMigrationRange(db, "20261101185119_EnforceFleetOccupationExclusivity", "20261030120000_ListImportedShipmentReferences");
            await FleetMigrationRange(db, "20261030120000_ListImportedShipmentReferences", "20261101185119_EnforceFleetOccupationExclusivity");
            Assert.Empty(await db.OcupacionesRuta.ToArrayAsync());
        }
        using (var factory = data.CreateApiFactory())
        using (var client = factory.CreateHttpClient())
        {
            await client.SetBearerForAccessAsync(data, p.AccesoId);
            var v = Assert.Single((await client.GetFromJsonAsync<VehiculoFlotaResponse[]>("/api/reparto/flota"))!);
            Assert.Null(v.VerificacionInicialEnUtc); Assert.Equal(1, v.CargaPendiente);
            var initial = new ComandoFlotaRequest(Guid.NewGuid(), null, v.Version, "Inspección física inicial.");
            using var rejected = await client.PostAsJsonAsync($"/api/reparto/flota/{v.Id}/verificar-inicial", initial);
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
            var cargo = Assert.Single(v.CargasIniciales);
            var receipt = initial with { ComandoId = Guid.NewGuid(), EnvioId = cargo.EnvioId, VersionEnvio = cargo.VersionEnvio };
            await Confirm(client, v.Id, "recibir-carga-inicial", receipt);
            await Confirm(client, v.Id, "recibir-carga-inicial", receipt);
            await Confirm(client, v.Id, "verificar-inicial", initial);
        }
        await using var audit = data.CreateDbContext();
        Assert.Equal(EstadoCustodiaBulto.Deposito, (await audit.Bultos.SingleAsync()).Custodia);
        Assert.Empty(await audit.OcupacionesRuta.ToArrayAsync());
        Assert.Single(await audit.HechosFlota.Where(h => h.Accion == "recibir-carga-inicial").ToArrayAsync());
    }

    [Fact]
    public async Task DifferentVehicleCannotBypassOccupiedCourier()
    {
        var clock = new FleetClock(DateTimeOffset.UtcNow); var data = await postgres.CreateDatabaseAsync();
        using var factory = new IntegrationApiFactory(data, timeProvider: clock); using var client = factory.CreateHttpClient();
        var p = await Preparar(data, client); var a = await Plan(client, p, FechaOperacion);
        using var dispatch = await client.PostAsJsonAsync($"/api/rutas/{a.Id}/despacho", new { a.Version });
        Assert.Equal(HttpStatusCode.OK, dispatch.StatusCode);
        using var create = await client.PutAsJsonAsync("/api/reparto/vehiculos", new { Matricula = "OTHER", PesoMaximoGramos = 3000m, VolumenMaximoCm3 = 2000m, Activo = true });
        var v = (await create.Content.ReadFromJsonAsync<VehiculoContract>())!;
        await Confirm(client, v.Id, "verificar-inicial", new(Guid.NewGuid(), null, 0, "Inspección de segundo vehículo vacío."));
        var shipment = await Shipment(data, client, p);
        var b = await Plan(client, shipment with { VehiculoId = v.Id }, FechaOperacion.AddDays(1));
        clock.Now = new DateTimeOffset(FechaOperacion.AddDays(1).ToDateTime(new TimeOnly(12,0)), TimeSpan.FromHours(-3));
        using var reject = await client.PostAsJsonAsync($"/api/rutas/{b.Id}/despacho", new { b.Version });
        Assert.Equal(HttpStatusCode.Conflict, reject.StatusCode); Assert.Contains(a.Id.ToString(), await reject.Content.ReadAsStringAsync());
        await using var db = data.CreateDbContext(); Assert.Single(await db.OcupacionesRuta.ToArrayAsync());
    }
}
