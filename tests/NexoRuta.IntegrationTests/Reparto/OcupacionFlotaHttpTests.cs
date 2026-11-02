using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.Domain.Envios;
using NexoRuta.IntegrationTests.Fixtures;
using static NexoRuta.IntegrationTests.Fixtures.RepartoHttpData;

namespace NexoRuta.IntegrationTests.Reparto;

public sealed partial class OcupacionFlotaHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task FuturePlanningIsAllowedButDispatchCannotAcquireBeforeItsOperationalDate()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var p = await Preparar(data, client);
        var future = FechaOperacion.AddDays(1);
        var route = await Plan(client, p, future);
        using var response = await client.PostAsJsonAsync($"/api/rutas/{route.Id}/despacho", new { route.Version });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("fecha operativa", await response.Content.ReadAsStringAsync());
        await using var db = data.CreateDbContext();
        var saved = await db.HojasRuta.SingleAsync();
        Assert.Equal("Planificada", saved.Estado.ToString()); Assert.Equal(route.Version, saved.Version);
    }

    [Fact]
    public async Task OccupiedVehicleAndCourierBlockDispatchAcrossMidnightWithoutBlockingFuturePlanning()
    {
        var clock = new FleetClock(DateTimeOffset.UtcNow);
        var data = await postgres.CreateDatabaseAsync();
        using var factory = new IntegrationApiFactory(data, timeProvider: clock); using var client = factory.CreateHttpClient();
        var p = await Preparar(data, client); var route = await Plan(client, p, FechaOperacion);
        using var dispatch = await client.PostAsJsonAsync($"/api/rutas/{route.Id}/despacho", new { route.Version });
        Assert.Equal(HttpStatusCode.OK, dispatch.StatusCode);
        await Op(client, route, 1, "InicioJornada");
        var second = await Shipment(data, client, p);
        var next = FechaOperacion.AddDays(1);
        var planned = await Plan(client, second, next);
        clock.Now = new DateTimeOffset(next.ToDateTime(new TimeOnly(12, 0)), TimeSpan.FromHours(-3));
        using var rejected = await client.PostAsJsonAsync($"/api/rutas/{planned.Id}/despacho", new { planned.Version });
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Contains(route.Id.ToString(), await rejected.Content.ReadAsStringAsync());
        await using var db = data.CreateDbContext();
        Assert.Equal("Planificada", (await db.HojasRuta.SingleAsync(r => r.Id == planned.Id)).Estado.ToString());
        Assert.Equal("EnCurso", (await db.HojasRuta.SingleAsync(r => r.Id == route.Id)).Estado.ToString());
    }

    [Fact]
    public async Task AuthorizedOfflineStartAndCloseSurviveMidnightButDoNotReleaseGlobally()
    {
        var clock = new FleetClock(DateTimeOffset.UtcNow); var data = await postgres.CreateDatabaseAsync();
        using var factory = new IntegrationApiFactory(data, timeProvider: clock); using var client = factory.CreateHttpClient();
        var p = await Preparar(data, client); var route = await Plan(client, p, FechaOperacion);
        using var dispatch = await client.PostAsJsonAsync($"/api/rutas/{route.Id}/despacho", new { route.Version });
        Assert.Equal(HttpStatusCode.OK, dispatch.StatusCode);
        clock.Now = new DateTimeOffset(FechaOperacion.AddDays(1).ToDateTime(new TimeOnly(12, 0)), TimeSpan.FromHours(-3));
        await Op(client, route, 1, "InicioJornada");
        await Op(client, route, 2, "FinJornada");
        await using var db = data.CreateDbContext(); var o = await db.OcupacionesRuta.SingleAsync();
        Assert.True(o.Activa); Assert.Null(o.RetornoEnUtc); Assert.Null(o.LiberadaEnUtc);
        Assert.Equal("Finalizada", (await db.HojasRuta.SingleAsync()).Estado.ToString());
    }

    private static async Task<HojaRutaResponse> Plan(HttpClient client, Preparado p, DateOnly date)
    {
        using var response = await client.PostAsJsonAsync("/api/rutas", new { RepartidorAccesoId = p.AccesoId, p.VehiculoId,
            Fecha = date, Paradas = new[] { new { p.EnvioId, LlegadaEstimadaUtc = new DateTimeOffset(date.ToDateTime(new TimeOnly(15, 0)), TimeSpan.Zero) } } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
    }

    private static async Task<Preparado> Shipment(IntegrationDatabase data, HttpClient client, Preparado p)
    {
        await using var db = data.CreateDbContext(); var first = await db.Envios.SingleAsync(e => e.Id == p.EnvioId);
        var second = new Envio(first.OperadorId, first.ComercioId, first.CreadoPorUsuarioId, first.DestinatarioId, first.DireccionId, first.VersionReglasId);
        db.Envios.Add(second); db.Bultos.Add(new Bulto(first.OperadorId, second.Id, "SECOND", 1000, 10, 10, 10)); await db.SaveChangesAsync();
        using var receive = await client.PostAsJsonAsync($"/api/deposito/envios/{second.Id}/recepcion", new { Version = 0 });
        Assert.Equal(HttpStatusCode.NoContent, receive.StatusCode);
        return new(p.AccesoId, p.VehiculoId, second.Id);
    }

    private static async Task<ResultadoOperacionResponse> Op(HttpClient client, HojaRutaResponse route, int sequence, string type,
        Guid? shipment = null, long? version = null, object? payload = null)
    {
        using var response = await client.PostAsJsonAsync($"/api/mobile/jornadas/{route.JornadaId}/operaciones", new
        { Id = Guid.NewGuid(), Secuencia = sequence, Tipo = type, EnvioId = shipment, VersionEnvio = version,
            OcurridaEn = DateTimeOffset.UtcNow, Datos = payload ?? new { } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<ResultadoOperacionResponse>())!;
        Assert.Equal("Aplicada", result.Resultado);
        return result;
    }

    private sealed class FleetClock(DateTimeOffset now) : TimeProvider
    { public DateTimeOffset Now { get; set; } = now; public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime(); }
}
