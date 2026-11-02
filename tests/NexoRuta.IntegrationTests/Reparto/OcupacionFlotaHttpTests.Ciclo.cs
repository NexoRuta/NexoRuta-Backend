using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.Domain.Envios;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;
using static NexoRuta.IntegrationTests.Fixtures.RepartoHttpData;
namespace NexoRuta.IntegrationTests.Reparto;

public sealed partial class OcupacionFlotaHttpTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseReturnReceiveReleaseReuseAreSeparateAuditableAndIdempotent(bool restricted)
    {
        var data = await postgres.CreateDatabaseAsync();
        using var owner = data.CreateApiFactory(); using var seed = owner.CreateHttpClient();
        var runtime = restricted ? await RlsRestrictedHttpFlowTests.RestrictApiAsync(data) : data;
        using var factory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = factory.CreateHttpClient();
        var p = await Preparar(data, client); var route = await Plan(client, p, FechaOperacion);
        using var dispatched = await client.PostAsJsonAsync($"/api/rutas/{route.Id}/despacho", new { route.Version });
        Assert.Equal(HttpStatusCode.OK, dispatched.StatusCode);
        await Op(client, route, 1, "InicioJornada");
        var stop = route.Paradas.Single();
        await Op(client, route, 2, "CargarBulto", p.EnvioId, stop.EnvioVersion, new { BultoId = stop.Bultos.Single().Id });
        await Op(client, route, 3, "ConfirmarCarga");
        await Op(client, route, 4, "FinJornada");
        var fleet = Assert.Single((await client.GetFromJsonAsync<VehiculoFlotaResponse[]>("/api/reparto/flota"))!);
        var occupation = Assert.Single(fleet.Ocupaciones);
        Assert.Equal("Finalizada", occupation.EstadoRuta); Assert.Equal(1, occupation.CargaPendiente); Assert.Null(occupation.RetornoEnUtc);
        // Administrative deactivation cannot release custody or prevent its resolution.
        using var inactive = await client.PutAsJsonAsync("/api/reparto/vehiculos", new { Id = p.VehiculoId, Matricula = "SAB1234", PesoMaximoGramos = 10000000m,
            VolumenMaximoCm3 = 100000000000m, Activo = false, Version = fleet.Version });
        Assert.Equal(HttpStatusCode.OK, inactive.StatusCode);
        var returned = new ComandoFlotaRequest(Guid.NewGuid(), occupation.Id, occupation.Version, "Inspección de regreso físico.");
        var ret = await Confirm(client, p.VehiculoId, "retorno", returned);
        Assert.Equal(JsonSerializer.Serialize(ret), JsonSerializer.Serialize(await Confirm(client, p.VehiculoId, "retorno", returned)));
        var release = new ComandoFlotaRequest(Guid.NewGuid(), occupation.Id, ret.Version, "Sin carga pendiente tras recepción.");
        using (var rejected = await client.PostAsJsonAsync($"/api/reparto/flota/{p.VehiculoId}/liberar", release))
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        await using (var db = data.CreateDbContext())
        {
            Assert.Null((await db.OcupacionesRuta.SingleAsync()).LiberadaEnUtc);
            Assert.Equal(EstadoCustodiaBulto.Repartidor, (await db.Bultos.SingleAsync()).Custodia);
        }
        var closed = (await client.GetFromJsonAsync<HojaRutaResponse>($"/api/mobile/rutas/{route.Id}"))!;
        Assert.Single(closed.IncidenciasCierre);
        using var receive = await client.PostAsJsonAsync($"/api/deposito/envios/{p.EnvioId}/recepcion", new { Version = closed.Paradas.Single().EnvioVersion });
        Assert.Equal(HttpStatusCode.NoContent, receive.StatusCode);
        var released = await Confirm(client, p.VehiculoId, "liberar", release);
        Assert.Equal(JsonSerializer.Serialize(released), JsonSerializer.Serialize(await Confirm(client, p.VehiculoId, "liberar", release)));
        var unchangedHistory = (await client.GetFromJsonAsync<HojaRutaResponse>($"/api/mobile/rutas/{route.Id}"))!;
        Assert.Equal(closed.IncidenciasCierre, unchangedHistory.IncidenciasCierre);
        fleet = Assert.Single((await client.GetFromJsonAsync<VehiculoFlotaResponse[]>("/api/reparto/flota"))!);
        Assert.Empty(fleet.Ocupaciones); Assert.NotNull(fleet.UltimaLiberacionEnUtc);
        using var active = await client.PutAsJsonAsync("/api/reparto/vehiculos", new { Id = p.VehiculoId, Matricula = "SAB1234", PesoMaximoGramos = 10000000m,
            VolumenMaximoCm3 = 100000000000m, Activo = true, Version = fleet.Version });
        Assert.Equal(HttpStatusCode.OK, active.StatusCode);
        var next = await Shipment(data, client, p); var second = await Plan(client, next, FechaOperacion);
        using var reuse = await client.PostAsJsonAsync($"/api/rutas/{second.Id}/despacho", new { second.Version });
        Assert.Equal(HttpStatusCode.OK, reuse.StatusCode);
        // A delayed exact replay returns its old receipt, but does not release B.
        await Confirm(client, p.VehiculoId, "liberar", release);
        using var stale = await client.PostAsJsonAsync($"/api/reparto/flota/{p.VehiculoId}/liberar", release with { ComandoId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        await using var audit = data.CreateDbContext();
        Assert.Equal(second.Id, (await audit.OcupacionesRuta.SingleAsync(o => o.LiberadaEnUtc == null && o.AnuladaEnUtc == null)).RutaId);
        Assert.Equal(1, await audit.HechosFlota.CountAsync(h => h.Accion == "retorno"));
        Assert.Equal(1, await audit.HechosFlota.CountAsync(h => h.Accion == "liberar"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task AnnulmentOnlyBeforeStartPreservesShipmentAndDoesNotInventReturn(int stage)
    {
        var data = await postgres.CreateDatabaseAsync(); using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var p = await Preparar(data, client); var route = await Plan(client, p, FechaOperacion);
        using var dispatch = await client.PostAsJsonAsync($"/api/rutas/{route.Id}/despacho", new { route.Version });
        Assert.Equal(HttpStatusCode.OK, dispatch.StatusCode);
        var started = stage > 0;
        if (started) await Op(client, route, 1, "InicioJornada");
        var stop = route.Paradas.Single();
        if (stage > 1) await Op(client, route, 2, "CargarBulto", p.EnvioId, stop.EnvioVersion, new { BultoId = stop.Bultos.Single().Id });
        if (stage > 2) await Op(client, route, 3, "ConfirmarCarga");
        var o = Assert.Single(Assert.Single((await client.GetFromJsonAsync<VehiculoFlotaResponse[]>("/api/reparto/flota"))!).Ocupaciones);
        var cmd = new ComandoFlotaRequest(Guid.NewGuid(), o.Id, o.Version, "Inspección: preparación no iniciada.");
        using var response = await client.PostAsJsonAsync($"/api/reparto/flota/{p.VehiculoId}/anular", cmd);
        Assert.Equal(started ? HttpStatusCode.Conflict : HttpStatusCode.OK, response.StatusCode);
        if (!started) await Confirm(client, p.VehiculoId, "anular", cmd);
        await using var db = data.CreateDbContext(); var saved = await db.OcupacionesRuta.SingleAsync();
        Assert.Null(saved.RetornoEnUtc); Assert.Null(saved.LiberadaEnUtc);
        Assert.Equal(started, saved.Activa); Assert.Equal(started ? "EnCurso" : "Cancelada", (await db.HojasRuta.SingleAsync()).Estado.ToString());
        Assert.Equal(stage > 2 ? EstadoEnvio.EnTransito : EstadoEnvio.AsignadoRuta, (await db.Envios.SingleAsync()).Estado);
        Assert.Equal(stage > 1 ? EstadoCustodiaBulto.Repartidor : EstadoCustodiaBulto.Deposito, (await db.Bultos.SingleAsync()).Custodia);
    }

    [Fact]
    public async Task ConcurrentDispatchAcquiresAtMostOneAndSqlExcludesVehicleAndCourierAcrossDates()
    {
        var clock = new FleetClock(DateTimeOffset.UtcNow);
        var data = await postgres.CreateDatabaseAsync(); using var factory = new IntegrationApiFactory(data, timeProvider: clock);
        using var client = factory.CreateHttpClient(); var p = await Preparar(data, client);
        var a = await Plan(client, p, FechaOperacion); var next = await Shipment(data, client, p);
        var b = await Plan(client, next, FechaOperacion.AddDays(1));
        // Two real requests for the same authorization: only one transaction may acquire it.
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => client.PostAsJsonAsync($"/api/rutas/{a.Id}/despacho", new { a.Version })));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK); Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        foreach (var r in responses) r.Dispose();
        await using var db = data.CreateDbContext();
        var other = await db.HojasRuta.Include(h => h.Paradas).SingleAsync(h => h.Id == b.Id);
        other.Despachar(other.Version); await db.SaveChangesAsync();
        var occupation = new NexoRuta.Domain.Reparto.OcupacionRuta(other, p.AccesoId, DateTimeOffset.UtcNow);
        db.OcupacionesRuta.Add(occupation);
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("23505", ((Npgsql.PostgresException)ex.InnerException!).SqlState);
    }

    private static async Task<ResultadoFlotaResponse> Confirm(HttpClient client, Guid vehicle, string action, ComandoFlotaRequest cmd)
    {
        using var response = await client.PostAsJsonAsync($"/api/reparto/flota/{vehicle}/{action}", cmd);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<ResultadoFlotaResponse>())!;
    }
}
