using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.Domain.Envios;
using NexoRuta.IntegrationTests.Fixtures;
using static NexoRuta.IntegrationTests.Fixtures.RepartoHttpData;

namespace NexoRuta.IntegrationTests.Reparto;

public sealed class CapacidadVigenteHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task LiveVehicleChecksCannotReadOrMutateAnotherOperatorsVehicle()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var p = await Preparar(data, client);
        var other = new NexoRuta.Domain.Administracion.Operador("Other test operator");
        var vehicle = new NexoRuta.Domain.Reparto.Vehiculo(other.Id, "SAB1234", 3000, 2000);
        await using (var db = data.CreateDbContext())
        { db.Operadores.Add(other); db.Vehiculos.Add(vehicle); await db.SaveChangesAsync(); }
        Assert.DoesNotContain((await client.GetFromJsonAsync<VehiculoContract[]>("/api/reparto/vehiculos"))!, x => x.Id == vehicle.Id);
        using var edit = await client.PutAsJsonAsync("/api/reparto/vehiculos", new { vehicle.Id, Version = 0, Matricula = "OTHER", PesoMaximoGramos = 1m, VolumenMaximoCm3 = 1m });
        Assert.Equal(HttpStatusCode.NotFound, edit.StatusCode);
        var before = await OperationalState(data);
        using var plan = await client.PostAsJsonAsync("/api/rutas", new { RepartidorAccesoId = p.AccesoId, VehiculoId = vehicle.Id, Fecha = FechaOperacion,
            Paradas = new[] { new { p.EnvioId, LlegadaEstimadaUtc = LlegadaEstimada } } });
        Assert.Equal(HttpStatusCode.Conflict, plan.StatusCode); Assert.Equal(before, await OperationalState(data));
        await using var verify = data.CreateDbContext();
        var saved = await verify.Vehiculos.SingleAsync(v => v.Id == vehicle.Id);
        Assert.Equal(0, saved.Version); Assert.Equal("SAB1234", saved.Matricula);
    }

    [Fact]
    public async Task GeneratedOpenApiDescribesEffectiveScaleOnVehicleAndPackageInputs()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = new IntegrationApiFactory(data, environment: "Development"); using var client = factory.CreateHttpClient();
        using var response = await client.GetAsync("/openapi/v1.json"); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var schemas = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("components").GetProperty("schemas");
        foreach (var (schema, fields) in new[]
        {
            ("VehiculoContract", new[] { "pesoMaximoGramos", "volumenMaximoCm3" }),
            ("CrearEnvioRequest", new[] { "pesoGramos", "largoCentimetros", "anchoCentimetros", "altoCentimetros" })
        })
            foreach (var field in fields) Assert.Equal(0.01m, schemas.GetProperty(schema).GetProperty("properties").GetProperty(field).GetProperty("multipleOf").GetDecimal());
    }

    [Theory]
    [InlineData(3000, 2000, 200, "")]
    [InlineData(2999.99, 2000, 409, "peso")]
    [InlineData(3000, 1999.99, 409, "volumen")]
    [InlineData(2999.99, 1999.99, 409, "peso y volumen")]
    public async Task PlanValidatesEveryPackageAndBothLimits(decimal weight, decimal volume, int status, string reason)
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var p = await PreparePackages(data, client);
        await Edit(client, p.VehiculoId, weight, volume);
        var before = await OperationalState(data);
        using var response = await client.PostAsJsonAsync("/api/rutas", Solicitud(p));
        Assert.Equal(status, (int)response.StatusCode);
        if (status == 200) Assert.Equal(2, (await response.Content.ReadFromJsonAsync<HojaRutaResponse>())!.Paradas.Single().Bultos.Count);
        else
        {
            var detail = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()!;
            Assert.Contains(reason, detail); Assert.Contains("g", detail); Assert.Contains("cm³", detail);
            Assert.Equal(before, await OperationalState(data));
        }
    }

    [Theory]
    [InlineData(1500, 1000, 409)]
    [InlineData(5000, 5000, 200)]
    public async Task DispatchUsesCurrentCapacityWithoutRewritingSnapshot(decimal weight, decimal volume, int status)
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var p = await PreparePackages(data, client); await Edit(client, p.VehiculoId, 3000, 2000);
        using var plan = await client.PostAsJsonAsync("/api/rutas", Solicitud(p));
        var route = (await plan.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
        await Edit(client, p.VehiculoId, weight, volume);
        var before = await OperationalState(data);
        using var dispatch = await client.PostAsJsonAsync($"/api/rutas/{route.Id}/despacho", new { route.Version });
        Assert.Equal(status, (int)dispatch.StatusCode);
        if (status == 409) Assert.Equal(before, await OperationalState(data));
        await using var db = data.CreateDbContext(); var saved = await db.HojasRuta.SingleAsync();
        Assert.Equal(3000, saved.VehiculoPesoMaximoGramos); Assert.Equal(2000, saved.VehiculoVolumenMaximoCm3);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ExitRejectsReducedOrInactiveVehicleAtomicallyIncludingPartialPhysicalLoad(bool active, bool complete)
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var p = await PreparePackages(data, client, blockDiscrepancies: false);
        using var plan = await client.PostAsJsonAsync("/api/rutas", Solicitud(p));
        var route = (await plan.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
        using var dispatch = await client.PostAsJsonAsync($"/api/rutas/{route.Id}/despacho", new { route.Version });
        Assert.Equal(HttpStatusCode.OK, dispatch.StatusCode);
        var sequence = 1;
        await Operation(client, route, sequence++, "InicioJornada");
        foreach (var b in route.Paradas.Single().Bultos.Take(complete ? 2 : 1))
            await Operation(client, route, sequence++, "CargarBulto", p.EnvioId, route.Paradas.Single().EnvioVersion, new { BultoId = b.Id });
        await Edit(client, p.VehiculoId, 500, 500, active);
        var before = await OperationalState(data, includeRouteVersion: false);
        var id = Guid.NewGuid();
        var result = await Operation(client, route, sequence, "ConfirmarCarga", id: id);
        Assert.Equal("Conflicto", result.Resultado);
        Assert.Equal(before, await OperationalState(data, includeRouteVersion: false));
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(await Operation(client, route, sequence, "ConfirmarCarga", id: id)));
        await using var db = data.CreateDbContext(); var saved = await db.HojasRuta.SingleAsync();
        Assert.Null(saved.SalidaEnUtc); Assert.True(saved.ReservaRecursosActiva);
        Assert.Equal("Conflicto", (await db.OperacionesJornada.SingleAsync(x => x.Id == id)).Resultado);
    }

    [Fact]
    public async Task ReassignmentUsesLiveCapacityAndPreservesBothRoutesOnRejection()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var p = await PreparePackages(data, client); await Edit(client, p.VehiculoId, 3000, 2000);
        using var source = await client.PostAsJsonAsync("/api/rutas", Solicitud(p));
        Assert.Equal(HttpStatusCode.OK, source.StatusCode);
        var tomorrow = FechaOperacion.AddDays(1);
        using var target = await client.PostAsJsonAsync("/api/rutas", new { RepartidorAccesoId = p.AccesoId, p.VehiculoId, Fecha = tomorrow, Paradas = Array.Empty<object>() });
        var route = (await target.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
        await Edit(client, p.VehiculoId, 1500, 1000);
        await using var db = data.CreateDbContext(); var version = await db.Envios.Where(x => x.Id == p.EnvioId).Select(x => x.Version).SingleAsync();
        var before = await OperationalState(data);
        using var response = await client.PostAsJsonAsync($"/api/envios/{p.EnvioId}/reasignacion", new
        {
            RutaDestinoId = route.Id, VersionRutaDestino = route.Version, VersionEnvio = version,
            LlegadaEstimadaUtc = new DateTimeOffset(tomorrow.ToDateTime(new TimeOnly(15, 0)), TimeSpan.Zero), Motivo = "Capacity regression"
        });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(before, await OperationalState(data));
    }

    [Fact]
    public async Task ExtremeVolumeIsAControlledConflictWithoutPersistingAssignments()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var p = await Preparar(data, client);
        await using (var db = data.CreateDbContext())
        {
            var op = (await db.Envios.SingleAsync()).OperadorId;
            for (var i = 0; i < 80; i++)
            {
                var b = new Bulto(op, p.EnvioId, $"EXTREME-{i}", 1, 999999999, 999999999, 999999999);
                b.RecibirEnDeposito(); db.Bultos.Add(b);
            }
            await db.SaveChangesAsync();
        }
        var before = await OperationalState(data);
        using var response = await client.PostAsJsonAsync("/api/rutas", Solicitud(p));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("volumen", await response.Content.ReadAsStringAsync());
        Assert.Equal(before, await OperationalState(data));
    }

    [Theory]
    [InlineData("1.005", false)]
    [InlineData("1.00", true)]
    [InlineData("1.000", true)]
    public async Task VehicleScaleRejectsSignificantDigitsAndRoundTripsExactly(string text, bool accepted)
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var p = await Preparar(data, client);
        var original = (await client.GetFromJsonAsync<VehiculoContract[]>("/api/reparto/vehiculos"))!.Single();
        var number = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        using var response = await client.PutAsJsonAsync("/api/reparto/vehiculos", new { original.Id, original.Version, original.Matricula, PesoMaximoGramos = number, VolumenMaximoCm3 = number, Activo = true });
        Assert.Equal(accepted ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
        var saved = (await client.GetFromJsonAsync<VehiculoContract[]>("/api/reparto/vehiculos"))!.Single();
        if (accepted)
        {
            var returned = (await response.Content.ReadFromJsonAsync<VehiculoContract>())!;
            Assert.Equal(number, returned.PesoMaximoGramos); Assert.Equal(returned.PesoMaximoGramos, saved.PesoMaximoGramos);
            Assert.Equal(number, saved.VolumenMaximoCm3);
        }
        else
        {
            var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
            Assert.True(errors.TryGetProperty("PesoMaximoGramos", out _)); Assert.True(errors.TryGetProperty("VolumenMaximoCm3", out _));
            Assert.Equal(original.Version, saved.Version); Assert.Equal(original.PesoMaximoGramos, saved.PesoMaximoGramos);
        }
    }

    private static async Task<Preparado> PreparePackages(IntegrationDatabase data, HttpClient client, bool blockDiscrepancies = true)
    {
        var p = await Preparar(data, client, bloquearDiscrepanciasCarga: blockDiscrepancies);
        await using var db = data.CreateDbContext(); var op = (await db.Envios.SingleAsync()).OperadorId;
        var b = new Bulto(op, p.EnvioId, "SECOND", 2000, 10, 10, 10); b.RecibirEnDeposito(); db.Bultos.Add(b); await db.SaveChangesAsync();
        return p;
    }

    private static async Task Edit(HttpClient client, Guid id, decimal weight, decimal volume, bool active = true)
    {
        var v = (await client.GetFromJsonAsync<VehiculoContract[]>("/api/reparto/vehiculos"))!.Single(x => x.Id == id);
        using var response = await client.PutAsJsonAsync("/api/reparto/vehiculos", new { v.Id, v.Version, v.Matricula, PesoMaximoGramos = weight, VolumenMaximoCm3 = volume, Activo = active });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<ResultadoOperacionResponse> Operation(HttpClient client, HojaRutaResponse route, int sequence, string type,
        Guid? shipment = null, long? version = null, object? data = null, Guid? id = null)
    {
        using var response = await client.PostAsJsonAsync($"/api/mobile/jornadas/{route.JornadaId}/operaciones", new
        { Id = id ?? Guid.NewGuid(), Secuencia = sequence, Tipo = type, EnvioId = shipment, VersionEnvio = version, OcurridaEn = LlegadaEstimada, Datos = data ?? new { } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ResultadoOperacionResponse>())!;
    }

    private static async Task<string> OperationalState(IntegrationDatabase data, bool includeRouteVersion = true)
    {
        await using var db = data.CreateDbContext();
        return JsonSerializer.Serialize(new
        {
            Envios = await db.Envios.OrderBy(x => x.Id).Select(x => new { x.Id, x.Estado, x.Version }).ToArrayAsync(),
            Bultos = await db.Bultos.OrderBy(x => x.Id).Select(x => new { x.Id, x.Custodia, x.RutaCustodiaId, x.AccesoCustodioId, x.Version }).ToArrayAsync(),
            Rutas = await db.HojasRuta.OrderBy(x => x.Id).Select(x => new { x.Id, x.Estado, x.SalidaEnUtc, x.ReservaRecursosActiva, Version = includeRouteVersion ? x.Version : 0 }).ToArrayAsync(),
            Paradas = await db.ParadasRuta.OrderBy(x => x.RutaId).ThenBy(x => x.EnvioId).Select(x => new { x.RutaId, x.EnvioId, x.Activa, x.MotivoSuspension }).ToArrayAsync()
        });
    }
}
