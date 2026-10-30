using System.Net;
using static NexoRuta.IntegrationTests.Fixtures.RepartoHttpData;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.Domain.Administracion;
using NexoRuta.Application.Envios.Dtos;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Reparto;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;

namespace NexoRuta.IntegrationTests.Reparto;

public sealed class RutasHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task BorradorVacioReservaRecursosPeroNoSeDespachaNiSeDescarga()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var p = await Preparar(data, client);
        var recursos = (await client.GetFromJsonAsync<RecursosPlanificacionResponse>("/api/rutas/recursos"))!;
        Assert.Contains(recursos.Repartidores, r => r.AccesoId == p.AccesoId);
        Assert.Contains(recursos.Vehiculos, v => v.Id == p.VehiculoId);
        using var incompleta = await client.PostAsJsonAsync("/api/rutas", new { RepartidorAccesoId = p.AccesoId,
            p.VehiculoId, Fecha = FechaOperacion });
        Assert.Equal(HttpStatusCode.BadRequest, incompleta.StatusCode);
        using var plan = await client.PostAsJsonAsync("/api/rutas", new { RepartidorAccesoId = p.AccesoId,
            p.VehiculoId, Fecha = FechaOperacion, Paradas = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.OK, plan.StatusCode);
        var hoja = (await plan.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
        Assert.Empty(hoja.Paradas);
        Assert.True(hoja.ReservaRecursosActiva);
        await using var dbVacia = data.CreateDbContext();
        Assert.True((await dbVacia.HojasRuta.AsNoTracking().SingleAsync(x => x.Id == hoja.Id)).ReservaRecursosActiva);
        using var duplicada = await client.PostAsJsonAsync("/api/rutas", new { RepartidorAccesoId = p.AccesoId,
            p.VehiculoId, Fecha = FechaOperacion, Paradas = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.Conflict, duplicada.StatusCode);
        using var detalleReserva = JsonDocument.Parse(await duplicada.Content.ReadAsStringAsync());
        var detail = detalleReserva.RootElement.GetProperty("detail").GetString()!;
        Assert.Contains(hoja.Id.ToString(), detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("repartidor", detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("vehículo", detail, StringComparison.OrdinalIgnoreCase);

        using var vehiculoAlternativo = await client.PutAsJsonAsync("/api/reparto/vehiculos", new
        { Matricula = "ALT4567", PesoMaximoGramos = 10000000m, VolumenMaximoCm3 = 100000000000m, Activo = true });
        Assert.Equal(HttpStatusCode.OK, vehiculoAlternativo.StatusCode);
        var idVehiculoAlternativo = (await vehiculoAlternativo.Content.ReadFromJsonAsync<VehiculoContract>())!.Id;
        using var ocupadoPorRepartidor = await client.PostAsJsonAsync("/api/rutas", new
        { RepartidorAccesoId = p.AccesoId, VehiculoId = idVehiculoAlternativo, Fecha = FechaOperacion, Paradas = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.Conflict, ocupadoPorRepartidor.StatusCode);
        Assert.Contains("repartidor", await ocupadoPorRepartidor.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var otroRepartidorId = await CrearRepartidorAlternativo(data, p.AccesoId);
        using var ocupadoPorVehiculo = await client.PostAsJsonAsync("/api/rutas", new
        { RepartidorAccesoId = otroRepartidorId, p.VehiculoId, Fecha = FechaOperacion, Paradas = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.Conflict, ocupadoPorVehiculo.StatusCode);
        Assert.Contains("vehículo", await ocupadoPorVehiculo.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        using var despacho = await client.PostAsJsonAsync($"/api/rutas/{hoja.Id}/despacho", new { hoja.Version });
        Assert.Equal(HttpStatusCode.Conflict, despacho.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<HojaRutaResponse[]>($"/api/mobile/rutas?fecha={FechaOperacion:yyyy-MM-dd}"))!);
        using var cancelarVacia = await client.PostAsJsonAsync($"/api/rutas/{hoja.Id}/cancelacion", new { hoja.Version });
        Assert.Equal(HttpStatusCode.OK, cancelarVacia.StatusCode);
        var cancelada = (await cancelarVacia.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
        Assert.Equal("Cancelada", cancelada.Estado);
        Assert.False(cancelada.ReservaRecursosActiva);
        using var despachoCancelada = await client.PostAsJsonAsync($"/api/rutas/{hoja.Id}/despacho", new { cancelada.Version });
        Assert.Equal(HttpStatusCode.Conflict, despachoCancelada.StatusCode);
        using var reservaNueva = await client.PostAsJsonAsync("/api/rutas", new { RepartidorAccesoId = p.AccesoId,
            p.VehiculoId, Fecha = FechaOperacion, Paradas = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.OK, reservaNueva.StatusCode);
        await using var db = data.CreateDbContext();
        Assert.Equal(EstadoEnvio.EnDeposito, (await db.Envios.SingleAsync(e => e.Id == p.EnvioId)).Estado);
        await client.SetBearerForAccessAsync(data, (await db.AccesosUsuario.SingleAsync(a => a.ComercioId != null)).Id);
        using var comercio = await client.GetAsync("/api/rutas/recursos");
        Assert.Equal(HttpStatusCode.Forbidden, comercio.StatusCode);
    }

    [Fact]
    public async Task RestrictedApiCancelsPlannedRouteAndReleasesItsReservation()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var setupFactory = data.CreateApiFactory();
        using var setupClient = setupFactory.CreateHttpClient();
        var prepared = await Preparar(data, setupClient);
        using var created = await setupClient.PostAsJsonAsync("/api/rutas", new
        {
            RepartidorAccesoId = prepared.AccesoId,
            prepared.VehiculoId,
            Fecha = FechaOperacion,
            Paradas = Array.Empty<object>()
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var original = (await created.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
        Assert.True(original.ReservaRecursosActiva);

        var restricted = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        using var restrictedFactory = new IntegrationApiFactory(restricted,
            settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = restrictedFactory.CreateHttpClient();
        await client.SetBearerForAccessAsync(data, prepared.AccesoId);

        var visible = (await client.GetFromJsonAsync<HojaRutaResponse[]>(
            $"/api/rutas?fecha={FechaOperacion:yyyy-MM-dd}"))!;
        var route = Assert.Single(visible);
        Assert.Equal(original.Id, route.Id);
        using var cancelled = await client.PostAsJsonAsync($"/api/rutas/{route.Id}/cancelacion",
            new { route.Version });
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        var result = (await cancelled.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
        Assert.Equal("Cancelada", result.Estado);
        Assert.False(result.ReservaRecursosActiva);

        using var replanned = await client.PostAsJsonAsync("/api/rutas", new
        {
            RepartidorAccesoId = prepared.AccesoId,
            prepared.VehiculoId,
            Fecha = FechaOperacion,
            Paradas = Array.Empty<object>()
        });
        Assert.Equal(HttpStatusCode.OK, replanned.StatusCode);
        var next = (await replanned.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
        Assert.True(next.ReservaRecursosActiva);

        await using var owner = data.CreateDbContext();
        var persisted = await owner.HojasRuta.AsNoTracking().Where(x => x.Id == original.Id).SingleAsync();
        Assert.Equal(EstadoHojaRuta.Cancelada, persisted.Estado);
        Assert.False(persisted.ReservaRecursosActiva);
        Assert.Empty(await owner.ParadasRuta.AsNoTracking().Where(x => x.RutaId == original.Id).ToArrayAsync());
        Assert.Equal(2, await owner.HojasRuta.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task ConcurrentRouteCancellationAndPlanningNeverDoubleReserveResources()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var setupFactory = data.CreateApiFactory();
        using var setupClient = setupFactory.CreateHttpClient();
        var prepared = await Preparar(data, setupClient);
        using var created = await setupClient.PostAsJsonAsync("/api/rutas", new
        {
            RepartidorAccesoId = prepared.AccesoId,
            prepared.VehiculoId,
            Fecha = FechaOperacion,
            Paradas = Array.Empty<object>()
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var original = (await created.Content.ReadFromJsonAsync<HojaRutaResponse>())!;

        var restricted = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        using var factory = new IntegrationApiFactory(restricted,
            settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var cancelClient = factory.CreateHttpClient();
        using var planClient = factory.CreateHttpClient();
        await cancelClient.SetBearerForAccessAsync(data, prepared.AccesoId);
        await planClient.SetBearerForAccessAsync(data, prepared.AccesoId);

        var cancellationTask = cancelClient.PostAsJsonAsync($"/api/rutas/{original.Id}/cancelacion",
            new { original.Version });
        var planningTask = planClient.PostAsJsonAsync("/api/rutas", new
        {
            RepartidorAccesoId = prepared.AccesoId,
            prepared.VehiculoId,
            Fecha = FechaOperacion,
            Paradas = Array.Empty<object>()
        });
        using var cancellation = await cancellationTask;
        using var planning = await planningTask;

        Assert.Equal(HttpStatusCode.OK, cancellation.StatusCode);
        Assert.Contains(planning.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
        await using var owner = data.CreateDbContext();
        var operatorId = await owner.AccesosUsuario.Where(a => a.Id == prepared.AccesoId)
            .Select(a => a.OperadorId!.Value).SingleAsync();
        var routes = await owner.HojasRuta.AsNoTracking()
            .Where(x => x.OperadorId == operatorId && x.Fecha == FechaOperacion)
            .ToArrayAsync();
        var cancelled = Assert.Single(routes, x => x.Id == original.Id);
        Assert.Equal(EstadoHojaRuta.Cancelada, cancelled.Estado);
        Assert.False(cancelled.ReservaRecursosActiva);
        Assert.True(routes.Count(x => x.ReservaRecursosActiva
            && x.Estado is not (EstadoHojaRuta.Finalizada or EstadoHojaRuta.Cancelada)) <= 1);
        Assert.True(routes.Count(x => x.RepartidorAccesoId == prepared.AccesoId
            && x.ReservaRecursosActiva && x.Estado is not (EstadoHojaRuta.Finalizada or EstadoHojaRuta.Cancelada)) <= 1);
        Assert.True(routes.Count(x => x.VehiculoId == prepared.VehiculoId
            && x.ReservaRecursosActiva && x.Estado is not (EstadoHojaRuta.Finalizada or EstadoHojaRuta.Cancelada)) <= 1);
        Assert.Equal(planning.StatusCode == HttpStatusCode.OK, routes.Length == 2);
    }

    [Fact]
    public async Task RecursosOcupadosEnOtraFechaNoBloqueanLaFechaSeleccionada()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var preparado = await Preparar(data, client);
        using var reservaAnterior = await client.PostAsJsonAsync("/api/rutas", new
        {
            RepartidorAccesoId = preparado.AccesoId, preparado.VehiculoId,
            Fecha = FechaOperacion, Paradas = Array.Empty<object>()
        });
        Assert.Equal(HttpStatusCode.OK, reservaAnterior.StatusCode);

        var fechaSeleccionada = FechaOperacion.AddDays(1);
        var llegada = new DateTimeOffset(fechaSeleccionada.ToDateTime(new TimeOnly(15, 0)), TimeSpan.Zero);
        using var plan = await client.PostAsJsonAsync("/api/rutas", new
        {
            RepartidorAccesoId = preparado.AccesoId, preparado.VehiculoId, Fecha = fechaSeleccionada,
            Paradas = new[] { new { preparado.EnvioId, LlegadaEstimadaUtc = llegada } }
        });

        Assert.Equal(HttpStatusCode.OK, plan.StatusCode);
        var hoja = (await plan.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
        Assert.Equal(fechaSeleccionada, hoja.Fecha);
        var rutas = await client.GetFromJsonAsync<HojaRutaResponse[]>($"/api/rutas?fecha={FechaOperacion:yyyy-MM-dd}");
        Assert.Single(rutas!);
    }

    [Fact]
    public async Task CancelarLaUltimaParadaConservaLaReservaHastaCancelarLaRuta()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var preparado = await Preparar(data, client);
        using var plan = await client.PostAsJsonAsync("/api/rutas", Solicitud(preparado));
        Assert.Equal(HttpStatusCode.OK, plan.StatusCode);
        var hojaOriginal = (await plan.Content.ReadFromJsonAsync<HojaRutaResponse>())!;

        await using var db = data.CreateDbContext();
        var comercio = await db.AccesosUsuario.SingleAsync(x => x.ComercioId != null);
        await client.SetBearerForAccessAsync(data, preparado.AccesoId);
        using var cancelarConParadaActiva = await client.PostAsJsonAsync($"/api/rutas/{hojaOriginal.Id}/cancelacion",
            new { hojaOriginal.Version });
        Assert.Equal(HttpStatusCode.Conflict, cancelarConParadaActiva.StatusCode);
        var rutaSinCambios = await db.HojasRuta.AsNoTracking().SingleAsync(x => x.Id == hojaOriginal.Id);
        Assert.Equal(EstadoHojaRuta.Planificada, rutaSinCambios.Estado);
        Assert.True(rutaSinCambios.ReservaRecursosActiva);
        Assert.Equal(1, rutaSinCambios.Version);
        var envioAntesCancelacion = await db.Envios.AsNoTracking().SingleAsync(x => x.Id == preparado.EnvioId);
        Assert.Equal(EstadoEnvio.AsignadoRuta, envioAntesCancelacion.Estado);
        await client.SetBearerForAccessAsync(data, comercio.Id);
        using var cancelarComoComercio = await client.PostAsJsonAsync($"/api/rutas/{hojaOriginal.Id}/cancelacion",
            new { hojaOriginal.Version });
        Assert.Equal(HttpStatusCode.Forbidden, cancelarComoComercio.StatusCode);
        Assert.Equal(EstadoHojaRuta.Planificada, (await db.HojasRuta.AsNoTracking().SingleAsync(x => x.Id == hojaOriginal.Id)).Estado);
        using var cancelacion = await client.PostAsJsonAsync($"/api/envios/{preparado.EnvioId}/cancelacion",
            new { envioAntesCancelacion.Version, Motivo = "Cancelación de prueba" });
        Assert.True(cancelacion.StatusCode == HttpStatusCode.OK, await cancelacion.Content.ReadAsStringAsync());

        var otroRepartidorId = await CrearRepartidorAlternativo(data, preparado.AccesoId);
        await client.SetBearerForAccessAsync(data, preparado.AccesoId);
        var rutaTrasCancelacionEnvio = (await client.GetFromJsonAsync<HojaRutaResponse[]>($"/api/rutas?fecha={FechaOperacion:yyyy-MM-dd}"))!
            .Single(x => x.Id == hojaOriginal.Id);
        Assert.Equal("Planificada", rutaTrasCancelacionEnvio.Estado);
        Assert.True(rutaTrasCancelacionEnvio.ReservaRecursosActiva);
        Assert.Single(rutaTrasCancelacionEnvio.Paradas);
        Assert.False(rutaTrasCancelacionEnvio.Paradas[0].Activa);
        using var nuevaPlanificacion = await client.PostAsJsonAsync("/api/rutas", new
        {
            RepartidorAccesoId = otroRepartidorId, preparado.VehiculoId, Fecha = FechaOperacion,
            Paradas = Array.Empty<object>()
        });
        Assert.Equal(HttpStatusCode.Conflict, nuevaPlanificacion.StatusCode);

        using var cancelacionObsoleta = await client.PostAsJsonAsync($"/api/rutas/{hojaOriginal.Id}/cancelacion",
            new { Version = hojaOriginal.Version });
        Assert.Equal(HttpStatusCode.Conflict, cancelacionObsoleta.StatusCode);
        Assert.True((await db.HojasRuta.AsNoTracking().SingleAsync(x => x.Id == hojaOriginal.Id)).ReservaRecursosActiva);

        using var cancelarRuta = await client.PostAsJsonAsync($"/api/rutas/{hojaOriginal.Id}/cancelacion",
            new { Version = rutaTrasCancelacionEnvio.Version });
        Assert.True(cancelarRuta.StatusCode == HttpStatusCode.OK, await cancelarRuta.Content.ReadAsStringAsync());
        var rutaCancelada = (await cancelarRuta.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
        Assert.Equal("Cancelada", rutaCancelada.Estado);
        Assert.False(rutaCancelada.ReservaRecursosActiva);
        Assert.Equal(rutaTrasCancelacionEnvio.Version + 1, rutaCancelada.Version);
        using var despachoCancelada = await client.PostAsJsonAsync($"/api/rutas/{hojaOriginal.Id}/despacho",
            new { rutaCancelada.Version });
        Assert.Equal(HttpStatusCode.Conflict, despachoCancelada.StatusCode);

        using var planificarRecursoLiberado = await client.PostAsJsonAsync("/api/rutas", new
        {
            RepartidorAccesoId = otroRepartidorId, preparado.VehiculoId, Fecha = FechaOperacion,
            Paradas = Array.Empty<object>()
        });
        Assert.Equal(HttpStatusCode.OK, planificarRecursoLiberado.StatusCode);

        var rutas = await client.GetFromJsonAsync<HojaRutaResponse[]>($"/api/rutas?fecha={FechaOperacion:yyyy-MM-dd}");
        Assert.Equal(2, rutas!.Length);
        var rutaAnterior = Assert.Single(rutas, x => x.Id == hojaOriginal.Id);
        Assert.Equal("Cancelada", rutaAnterior.Estado);
        Assert.False(rutaAnterior.ReservaRecursosActiva);
        Assert.Single(rutaAnterior.Paradas);
        Assert.False(rutaAnterior.Paradas[0].Activa);
        Assert.False((await db.HojasRuta.AsNoTracking().SingleAsync(x => x.Id == hojaOriginal.Id)).ReservaRecursosActiva);
        Assert.Equal(EstadoEnvio.Cancelado, (await db.Envios.AsNoTracking().SingleAsync(x => x.Id == preparado.EnvioId)).Estado);
        Assert.Single(await db.CancelacionesEnvio.AsNoTracking().Where(x => x.EnvioId == preparado.EnvioId).ToArrayAsync());
    }

    [Fact]
    public async Task RutaCanceladaNoPuedeRecibirReasignacionesNiDespacharse()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var original = await Preparar(data, client);
        using var planOriginal = await client.PostAsJsonAsync("/api/rutas", Solicitud(original));
        Assert.Equal(HttpStatusCode.OK, planOriginal.StatusCode);
        var rutaOriginal = (await planOriginal.Content.ReadFromJsonAsync<HojaRutaResponse>())!;

        await using var db = data.CreateDbContext();
        var comercio = await db.AccesosUsuario.SingleAsync(x => x.ComercioId != null);
        var envioOriginal = await db.Envios.AsNoTracking().SingleAsync(x => x.Id == original.EnvioId);
        await client.SetBearerForAccessAsync(data, comercio.Id);
        using var cancelacion = await client.PostAsJsonAsync($"/api/envios/{original.EnvioId}/cancelacion",
            new { envioOriginal.Version, Motivo = "Liberar la ruta de prueba" });
        Assert.True(cancelacion.StatusCode == HttpStatusCode.OK, await cancelacion.Content.ReadAsStringAsync());

        await using var secondShipmentSetup = data.CreateDbContext();
        var operatorAccess = await secondShipmentSetup.AccesosUsuario.SingleAsync(x => x.OperadorId != null);
        var ruleSetId = await secondShipmentSetup.ReglasOperativasOperadores
            .Where(x => x.OperadorId == operatorAccess.OperadorId)
            .Select(x => x.VersionVigenteId).SingleAsync();
        var destinatario = new Destinatario(operatorAccess.OperadorId!.Value, "Segundo destinatario", "segundo@example.test");
        var direccion = new Direccion(operatorAccess.OperadorId.Value, "Calle de prueba 456");
        var segundoEnvio = new Envio(operatorAccess.OperadorId.Value, comercio.ComercioId!.Value,
            comercio.UsuarioId, destinatario.Id, direccion.Id, ruleSetId!.Value);
        secondShipmentSetup.AddRange(destinatario, direccion, segundoEnvio,
            new Bulto(operatorAccess.OperadorId.Value, segundoEnvio.Id, "RUTA-TEST-2", 1000, 10, 10, 10));
        await secondShipmentSetup.SaveChangesAsync();
        await client.SetBearerForAccessAsync(data, comercio.Id);
        using var destinoSegundo = await client.PutAsJsonAsync($"/api/envios/{segundoEnvio.Id}/destino",
            new { Version = 0, Latitud = -34.9, Longitud = -56.2 });
        Assert.Equal(HttpStatusCode.NoContent, destinoSegundo.StatusCode);
        await client.SetBearerForAccessAsync(data, original.AccesoId);
        using var recepcionSegunda = await client.PostAsJsonAsync($"/api/deposito/envios/{segundoEnvio.Id}/recepcion", new { Version = 0 });
        Assert.Equal(HttpStatusCode.NoContent, recepcionSegunda.StatusCode);
        var otroRepartidor = await CrearRepartidorAlternativo(data, original.AccesoId);
        var rutaVaciada = (await client.GetFromJsonAsync<HojaRutaResponse[]>($"/api/rutas?fecha={FechaOperacion:yyyy-MM-dd}"))!
            .Single(x => x.Id == rutaOriginal.Id);
        Assert.True(rutaVaciada.ReservaRecursosActiva);
        using var planMientrasReservada = await client.PostAsJsonAsync("/api/rutas", new
        {
            RepartidorAccesoId = otroRepartidor, VehiculoId = original.VehiculoId, Fecha = FechaOperacion,
            Paradas = Array.Empty<object>()
        });
        Assert.Equal(HttpStatusCode.Conflict, planMientrasReservada.StatusCode);

        using var cancelacionRuta = await client.PostAsJsonAsync($"/api/rutas/{rutaVaciada.Id}/cancelacion",
            new { Version = rutaVaciada.Version });
        Assert.True(cancelacionRuta.StatusCode == HttpStatusCode.OK, await cancelacionRuta.Content.ReadAsStringAsync());
        var rutaCancelada = (await cancelacionRuta.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
        Assert.Equal("Cancelada", rutaCancelada.Estado);
        Assert.False(rutaCancelada.ReservaRecursosActiva);
        using var despacho = await client.PostAsJsonAsync($"/api/rutas/{rutaCancelada.Id}/despacho", new { rutaCancelada.Version });
        Assert.Equal(HttpStatusCode.Conflict, despacho.StatusCode);

        using var rutaSegundoEnvio = await client.PostAsJsonAsync("/api/rutas", new
        {
            RepartidorAccesoId = otroRepartidor,
            VehiculoId = original.VehiculoId,
            Fecha = FechaOperacion,
            Paradas = new[] { new { EnvioId = segundoEnvio.Id, LlegadaEstimadaUtc = LlegadaEstimada } }
        });
        Assert.Equal(HttpStatusCode.OK, rutaSegundoEnvio.StatusCode);

        var rutaDestino = (await client.GetFromJsonAsync<HojaRutaResponse[]>($"/api/rutas?fecha={FechaOperacion:yyyy-MM-dd}"))!
            .Single(x => x.Id == rutaOriginal.Id);
        var versionEnvio = await db.Envios.AsNoTracking().Where(x => x.Id == segundoEnvio.Id).Select(x => x.Version).SingleAsync();
        var outboxAntes = await db.OutboxMessages.AsNoTracking().CountAsync(x => x.EnvioId == segundoEnvio.Id);
        using var reasignacion = await client.PostAsJsonAsync($"/api/envios/{segundoEnvio.Id}/reasignacion", new
        {
            RutaDestinoId = rutaDestino.Id,
            VersionEnvio = versionEnvio,
            VersionRutaDestino = rutaDestino.Version,
            LlegadaEstimadaUtc = LlegadaEstimada,
            Motivo = "No reutilizar una reserva liberada"
        });

        Assert.Equal(HttpStatusCode.Conflict, reasignacion.StatusCode);
        await using var verificacion = data.CreateDbContext();
        Assert.Equal(versionEnvio, await verificacion.Envios.AsNoTracking().Where(x => x.Id == segundoEnvio.Id).Select(x => x.Version).SingleAsync());
        Assert.Single(await verificacion.ParadasRuta.AsNoTracking().Where(x => x.EnvioId == segundoEnvio.Id && x.Activa).ToArrayAsync());
        Assert.Equal(outboxAntes, await verificacion.OutboxMessages.AsNoTracking().CountAsync(x => x.EnvioId == segundoEnvio.Id));
        Assert.Equal(EstadoHojaRuta.Cancelada, (await verificacion.HojasRuta.AsNoTracking().SingleAsync(x => x.Id == rutaOriginal.Id)).Estado);
        Assert.False((await verificacion.HojasRuta.AsNoTracking().SingleAsync(x => x.Id == rutaOriginal.Id)).ReservaRecursosActiva);
        Assert.True((await verificacion.HojasRuta.AsNoTracking().SingleAsync(x => x.Paradas.Any(p => p.EnvioId == segundoEnvio.Id))).ReservaRecursosActiva);
    }

    [Fact]
    public async Task CancelacionDeLaUltimaParadaYPlanificacionConcurrentesConservanLaReservaDelOperador()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory();
        using var planificador = factory.CreateHttpClient(); using var cancelador = factory.CreateHttpClient();
        var preparado = await Preparar(data, planificador);
        using var planInicial = await planificador.PostAsJsonAsync("/api/rutas", Solicitud(preparado));
        Assert.Equal(HttpStatusCode.OK, planInicial.StatusCode);
        var rutaOriginal = (await planInicial.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
        await using var db = data.CreateDbContext();
        var comercio = await db.AccesosUsuario.SingleAsync(x => x.ComercioId != null);
        var envioAntesCancelacion = await db.Envios.AsNoTracking().SingleAsync(x => x.Id == preparado.EnvioId);
        Assert.Equal(EstadoEnvio.AsignadoRuta, envioAntesCancelacion.Estado);
        await cancelador.SetBearerForAccessAsync(data, comercio.Id);

        var cancelacionTask = cancelador.PostAsJsonAsync($"/api/envios/{preparado.EnvioId}/cancelacion",
            new { envioAntesCancelacion.Version, Motivo = "Cancelación concurrente de prueba" });
        var planificacionTask = planificador.PostAsJsonAsync("/api/rutas", new
        {
            RepartidorAccesoId = preparado.AccesoId, preparado.VehiculoId, Fecha = FechaOperacion,
            Paradas = Array.Empty<object>()
        });
        using var cancelacion = await cancelacionTask;
        using var planConcurrente = await planificacionTask;
        Assert.True(cancelacion.StatusCode == HttpStatusCode.OK, await cancelacion.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, planConcurrente.StatusCode);

        var rutas = await planificador.GetFromJsonAsync<HojaRutaResponse[]>($"/api/rutas?fecha={FechaOperacion:yyyy-MM-dd}");
        var anterior = Assert.Single(rutas!);
        Assert.Equal(rutaOriginal.Id, anterior.Id);
        Assert.Equal("Planificada", anterior.Estado);
        Assert.True(anterior.ReservaRecursosActiva);
        Assert.All(anterior.Paradas, p => Assert.False(p.Activa));
    }

    private static async Task<Guid> CrearRepartidorAlternativo(IntegrationDatabase data, Guid accesoBaseId)
    {
        await using var db = data.CreateDbContext();
        var accesoBase = await db.AccesosUsuario.SingleAsync(x => x.Id == accesoBaseId);
        var rol = await db.Roles.SingleAsync(x => x.Id == accesoBase.RolId);
        var usuario = new Usuario("repartidor-alternativo@example.test");
        var acceso = new AccesoUsuario(usuario.Id, accesoBase.OperadorId, null, rol);
        db.AddRange(usuario, acceso);
        await db.SaveChangesAsync();
        db.Add(new Repartidor(acceso.Id, acceso.OperadorId!.Value, "Repartidor alternativo"));
        await db.SaveChangesAsync();
        return acceso.Id;
    }
    [Fact]
    public async Task RecibirPlanificarDespacharYDescargarConservaVersionPoliticaYDestino()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var preparado = await Preparar(data, client);
        using var plan = await client.PostAsJsonAsync("/api/rutas", Solicitud(preparado));
        Assert.Equal(HttpStatusCode.OK, plan.StatusCode);
        var hoja = (await plan.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
        Assert.Equal("Planificada", hoja.Estado);
        Assert.Single(hoja.Paradas);
        Assert.Equal((-34.9, -56.2), (hoja.Paradas[0].Latitud, hoja.Paradas[0].Longitud));
        Assert.Empty((await client.GetFromJsonAsync<HojaRutaResponse[]>($"/api/mobile/rutas?fecha={FechaOperacion:yyyy-MM-dd}"))!);
        using var despacho = await client.PostAsJsonAsync($"/api/rutas/{hoja.Id}/despacho", new { hoja.Version });
        Assert.Equal(HttpStatusCode.OK, despacho.StatusCode);
        var propia = Assert.Single((await client.GetFromJsonAsync<HojaRutaResponse[]>($"/api/mobile/rutas?fecha={FechaOperacion:yyyy-MM-dd}"))!);
        Assert.Equal("Despachada", propia.Estado);
        Assert.Equal(preparado.AccesoId, propia.RepartidorAccesoId);
        Assert.Equal(1, propia.Politica.Version);
        Assert.All(propia.Paradas[0].Bultos, x => Assert.Equal("Deposito", x.Custodia));
        using var antiguo = await client.PostAsJsonAsync($"/api/rutas/{hoja.Id}/despacho", new { hoja.Version });
        Assert.Equal(HttpStatusCode.Conflict, antiguo.StatusCode);
        await using var db = data.CreateDbContext();
        Assert.Equal(EstadoEnvio.AsignadoRuta, (await db.Envios.SingleAsync(x => x.Id == preparado.EnvioId)).Estado);
    }

    [Fact]
    public async Task ConfiguracionExplicitaYCapacidadSonObligatoriasSinAsignarSiFalla()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var preparado = await Preparar(data, client, politica: false);
        using var sinPolitica = await client.PostAsJsonAsync("/api/rutas", Solicitud(preparado));
        Assert.Equal(HttpStatusCode.Conflict, sinPolitica.StatusCode);
        using var config = await client.PutAsJsonAsync("/api/reparto/configuracion", Politica());
        Assert.Equal(HttpStatusCode.OK, config.StatusCode);
        await using var db = data.CreateDbContext();
        var vehiculo = await db.Vehiculos.SingleAsync(x => x.Id == preparado.VehiculoId);
        vehiculo.Actualizar(vehiculo.Matricula, 0.01m, 0.01m, true); await db.SaveChangesAsync();
        using var exceso = await client.PostAsJsonAsync("/api/rutas", Solicitud(preparado));
        Assert.Equal(HttpStatusCode.Conflict, exceso.StatusCode);
        Assert.Empty(await db.HojasRuta.ToArrayAsync());
        Assert.Equal(EstadoEnvio.EnDeposito, (await db.Envios.SingleAsync(x => x.Id == preparado.EnvioId)).Estado);
    }

    [Fact]
    public async Task DosPlanificacionesConcurrentesSoloAsignanUnaVez()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var preparado = await Preparar(data, client);
        var respuestas = await Task.WhenAll(client.PostAsJsonAsync("/api/rutas", Solicitud(preparado)),
            client.PostAsJsonAsync("/api/rutas", Solicitud(preparado)));
        Assert.Equal(1, respuestas.Count(x => x.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, respuestas.Count(x => x.StatusCode == HttpStatusCode.Conflict));
        foreach (var respuesta in respuestas) respuesta.Dispose();
        await using var db = data.CreateDbContext(); Assert.Single(await db.HojasRuta.ToArrayAsync());
    }

    [Fact]
    public async Task ComercioNoDespachaYCoordenadasNoSeCambianTrasRecepcion()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var preparado = await Preparar(data, client);
        await using var db = data.CreateDbContext();
        await client.SetBearerForAccessAsync(data, (await db.AccesosUsuario.SingleAsync(x => x.ComercioId != null)).Id);
        using var plan = await client.PostAsJsonAsync("/api/rutas", Solicitud(preparado));
        Assert.Equal(HttpStatusCode.Forbidden, plan.StatusCode);
        using var cambio = await client.PutAsJsonAsync($"/api/envios/{preparado.EnvioId}/destino", new { Version = 1, Latitud = -34.1, Longitud = -56.1 });
        Assert.Equal(HttpStatusCode.Conflict, cambio.StatusCode);
        using var propias = await client.GetAsync($"/api/mobile/rutas?fecha={FechaOperacion:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.Forbidden, propias.StatusCode);
    }

    [Fact]
    public async Task ComercioConfirmaDestinoPendienteTrasRecepcionYPermitePlanificarSinCambiarDestinoAsignado()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var preparado = await Preparar(data, client, confirmar: false);
        using var planSinDestino = await client.PostAsJsonAsync("/api/rutas", Solicitud(preparado));
        Assert.Equal(HttpStatusCode.Conflict, planSinDestino.StatusCode);

        await using var db = data.CreateDbContext();
        var comercio = await db.AccesosUsuario.SingleAsync(x => x.ComercioId != null);
        await client.SetBearerForAccessAsync(data, comercio.Id);
        var url = $"/api/envios/{preparado.EnvioId}/destino";
        var destino = await client.GetFromJsonAsync<DestinoEnvioDatos>(url);
        Assert.NotNull(destino);
        Assert.Null(destino.ConfirmadaEnUtc);
        using var confirmar = await client.PutAsJsonAsync(url, new { destino.Version, Latitud = -34.9, Longitud = -56.2 });
        Assert.Equal(HttpStatusCode.NoContent, confirmar.StatusCode);

        await client.SetBearerForAccessAsync(data, preparado.AccesoId);
        using var planConDestino = await client.PostAsJsonAsync("/api/rutas", Solicitud(preparado));
        Assert.Equal(HttpStatusCode.OK, planConDestino.StatusCode);
        var hoja = (await planConDestino.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
        Assert.Equal("AsignadoRuta", (await db.Envios.AsNoTracking().SingleAsync(x => x.Id == preparado.EnvioId)).Estado.ToString());

        await client.SetBearerForAccessAsync(data, comercio.Id);
        using var cambiarAsignado = await client.PutAsJsonAsync(url, new { Version = 1, Latitud = -34.8, Longitud = -56.1 });
        Assert.Equal(HttpStatusCode.Conflict, cambiarAsignado.StatusCode);
        var direccion = await db.Direcciones.AsNoTracking().SingleAsync();
        Assert.Equal(1, direccion.Version);
        Assert.Equal((-34.9, -56.2), (direccion.Latitud, direccion.Longitud));
        Assert.Equal("AsignadoRuta", (await db.Envios.AsNoTracking().SingleAsync(x => x.Id == preparado.EnvioId)).Estado.ToString());
    }

}
