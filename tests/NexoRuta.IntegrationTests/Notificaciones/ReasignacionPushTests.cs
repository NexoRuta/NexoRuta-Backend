using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Reparto;
using NexoRuta.Infrastructure.Outbox;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.Worker;
using static NexoRuta.IntegrationTests.Fixtures.RepartoHttpData;

namespace NexoRuta.IntegrationTests.Notificaciones;

public sealed partial class PushOutboxTests
{
    [Fact]
    public async Task ReasignacionAntesDeLaSalidaMantieneEstadoYCustodiaYNotificaAlRepartidorActual()
    {
        using var p = await PrepararPush(1, registrarAviso: false);
        var origen = (await p.Client.GetFromJsonAsync<HojaRutaResponse>($"/api/mobile/rutas/{p.Aviso.RutaId}"))!;
        var parada = origen.Paradas[0];
        using (var listedResponse = await p.Client.GetAsync("/api/envios"))
        {
            Assert.Equal(HttpStatusCode.OK, listedResponse.StatusCode);
            using var listed = System.Text.Json.JsonDocument.Parse(await listedResponse.Content.ReadAsStringAsync());
            var shipment = listed.RootElement.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == parada.EnvioId);
            Assert.Equal("Deposito", shipment.GetProperty("bultos")[0].GetProperty("custodia").GetString());
        }
        Assert.Equal("Aplicada", (await Operar(p.Client, origen, 1, "InicioJornada")).Resultado);
        var versionSinRed = parada.EnvioVersion;
        var destino = await CrearDestino(p, vacia: true);
        using var mover = await p.Client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/reasignacion", new
        {
            RutaDestinoId = destino.Id, VersionEnvio = versionSinRed, VersionRutaDestino = destino.Version,
            LlegadaEstimadaUtc = LlegadaEstimada.AddDays(1).AddHours(1), Motivo = "Postergar la entrega para mañana."
        });
        Assert.Equal(HttpStatusCode.OK, mover.StatusCode);
        var nueva = (await mover.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
        Assert.Single(nueva.Paradas);
        await using (var db = p.Data.CreateDbContext())
        {
            var bulto = await db.Bultos.SingleAsync(b => b.Id == parada.Bultos[0].Id);
            Assert.Equal(EstadoCustodiaBulto.Deposito, bulto.Custodia);
            var envio = await db.Envios.SingleAsync(e => e.Id == parada.EnvioId);
            Assert.Equal(EstadoEnvio.AsignadoRuta, envio.Estado);
            Assert.Equal(versionSinRed + 1, envio.Version);
            Assert.Single(await db.EnvioEventos.Where(e => e.EnvioId == parada.EnvioId && e.Tipo == "Hecho"
                && e.OrigenTipo == "ReasignacionRuta" && e.VersionAnterior == versionSinRed
                && e.VersionNueva == versionSinRed + 1).ToArrayAsync());
            Assert.Equal(1, await db.EnvioEventos.CountAsync(e => e.EnvioId == parada.EnvioId && e.Tipo == "TransicionEstado"
                && e.EstadoAnterior == "EnDeposito" && e.EstadoNuevo == "AsignadoRuta"));
            var aviso = Assert.Single(await db.AvisosReparto.ToArrayAsync());
            Assert.Equal((p.Aviso.AccesoId, origen.Id, "Reasignacion"), (aviso.AccesoId, aviso.RutaId, aviso.Tipo));
            Assert.Equal(1, await db.OutboxMessages.CountAsync(m => m.Destino == OutboxDestino.PushFcm));
            Assert.False((await db.ParadasRuta.SingleAsync(x => x.RutaId == origen.Id && x.EnvioId == envio.Id)).Activa);
            Assert.True((await db.ParadasRuta.SingleAsync(x => x.RutaId == nueva.Id && x.EnvioId == envio.Id)).Activa);
        }
        await using var services = await ServiciosWorker(p);
        var transporte = new Transporte();
        using var worker = new PushOutboxWorker(services.GetRequiredService<IServiceScopeFactory>(), transporte, NullLogger<PushOutboxWorker>.Instance);
        Assert.True(await worker.ProcesarSiguienteAsync(default));
        Assert.Equal(p.Fids, Assert.Single(transporte.Enviados).Fids);

        using var final = await p.Client.PostAsJsonAsync($"/api/rutas/{nueva.Id}/despacho", new { nueva.Version });
        Assert.Equal(HttpStatusCode.OK, final.StatusCode);
    }

    [Fact]
    public async Task EnTransitoNoSeReasignaYLaRutaOriginalConservaLaEntrega()
    {
        using var p = await PrepararPush(0, registrarAviso: false);
        var origen = (await p.Client.GetFromJsonAsync<HojaRutaResponse>($"/api/mobile/rutas/{p.Aviso.RutaId}"))!;
        var parada = origen.Paradas[0];
        Assert.Equal("Aplicada", (await Operar(p.Client, origen, 1, "InicioJornada")).Resultado);
        Assert.Equal("Aplicada", (await Operar(p.Client, origen, 2, "CargarBulto", parada.EnvioId, parada.EnvioVersion, new { BultoId = parada.Bultos[0].Id })).Resultado);
        Assert.Equal("Aplicada", (await Operar(p.Client, origen, 3, "ConfirmarCarga")).Resultado);
        var cargada = (await p.Client.GetFromJsonAsync<HojaRutaResponse>($"/api/mobile/rutas/{origen.Id}"))!;
        var versionSinRed = cargada.Paradas[0].EnvioVersion;
        var destino = await CrearDestino(p, vacia: true);
        using var mover = await p.Client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/reasignacion", new
        {
            RutaDestinoId = destino.Id, VersionEnvio = versionSinRed, VersionRutaDestino = destino.Version,
            LlegadaEstimadaUtc = LlegadaEstimada.AddDays(1).AddHours(1), Motivo = "Postergar para mañana."
        });
        Assert.Equal(HttpStatusCode.Conflict, mover.StatusCode);
        Assert.Contains("En tránsito", await mover.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        await using (var beforeDelivery = p.Data.CreateDbContext())
        {
            Assert.True((await beforeDelivery.ParadasRuta.SingleAsync(x => x.RutaId == origen.Id && x.EnvioId == parada.EnvioId)).Activa);
            Assert.Empty(await beforeDelivery.ParadasRuta.Where(x => x.RutaId == destino.Id && x.EnvioId == parada.EnvioId).ToArrayAsync());
            Assert.Empty(await beforeDelivery.AvisosReparto.ToArrayAsync());
            Assert.Empty(await beforeDelivery.OutboxMessages.Where(x => x.Destino == OutboxDestino.PushFcm).ToArrayAsync());
        }
        var entrega = await Operar(p.Client, origen, 4, "EntregaFisica", parada.EnvioId, versionSinRed, new { Nombre = "Receptor sin conexión" });
        Assert.Equal("Aplicada", entrega.Resultado);
        Assert.Equal("EntregaPendienteValidacion", entrega.EstadosEnvio[parada.EnvioId]);
        await using (var db = p.Data.CreateDbContext())
        {
            var envio = await db.Envios.SingleAsync(e => e.Id == parada.EnvioId);
            Assert.NotNull(envio.EntregaFisicaEnUtc);
            Assert.Equal(EstadoCustodiaBulto.Destinatario, (await db.Bultos.SingleAsync(b => b.Id == parada.Bultos[0].Id)).Custodia);
            Assert.Equal(versionSinRed + 1, envio.Version);
            Assert.False((await db.ParadasRuta.SingleAsync(x => x.RutaId == origen.Id && x.EnvioId == envio.Id)).Activa);
            Assert.Empty(await db.ParadasRuta.Where(x => x.RutaId == destino.Id && x.EnvioId == envio.Id).ToArrayAsync());
            Assert.Empty(await db.AvisosReparto.ToArrayAsync());
            Assert.Empty(await db.OutboxMessages.Where(x => x.Destino == OutboxDestino.PushFcm).ToArrayAsync());
        }
        Assert.Equal("Aplicada", (await Operar(p.Client, origen, 5, "FinJornada")).Resultado);
        await using var final = p.Data.CreateDbContext();
        Assert.Equal(EstadoEnvio.EntregaPendienteValidacion, (await final.Envios.SingleAsync(e => e.Id == parada.EnvioId)).Estado);
        Assert.Contains("Receptor sin conexión", (await final.OperacionesJornada.SingleAsync(x => x.RutaId == origen.Id && x.Secuencia == 4)).Payload);
    }

    [Fact]
    public async Task ReasignarBultoCargadoAntesDeConfirmarSalidaNoTransfiereLaCustodia()
    {
        using var p = await PrepararPush(0, registrarAviso: false);
        var origen = (await p.Client.GetFromJsonAsync<HojaRutaResponse>($"/api/mobile/rutas/{p.Aviso.RutaId}"))!;
        var parada = origen.Paradas[0];
        Assert.Equal("Aplicada", (await Operar(p.Client, origen, 1, "InicioJornada")).Resultado);
        Assert.Equal("Aplicada", (await Operar(p.Client, origen, 2, "CargarBulto", parada.EnvioId, parada.EnvioVersion,
            new { BultoId = parada.Bultos[0].Id })).Resultado);
        var destino = await CrearDestino(p, vacia: true);

        using var response = await p.Client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/reasignacion", new
        {
            RutaDestinoId = destino.Id, VersionEnvio = parada.EnvioVersion, VersionRutaDestino = destino.Version,
            LlegadaEstimadaUtc = LlegadaEstimada.AddDays(1).AddHours(1), Motivo = "Cambió la planificación."
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await using var db = p.Data.CreateDbContext();
        Assert.Equal(EstadoEnvio.AsignadoRuta, (await db.Envios.SingleAsync(e => e.Id == parada.EnvioId)).Estado);
        Assert.Equal(EstadoCustodiaBulto.Repartidor, (await db.Bultos.SingleAsync(b => b.Id == parada.Bultos[0].Id)).Custodia);
        Assert.True((await db.ParadasRuta.SingleAsync(x => x.RutaId == origen.Id && x.EnvioId == parada.EnvioId)).Activa);
        Assert.Empty(await db.ParadasRuta.Where(x => x.RutaId == destino.Id && x.EnvioId == parada.EnvioId).ToArrayAsync());
    }

    [Fact]
    public async Task DestinoConVersionIncorrectaNoSuspendeNiEncolaAvisos()
    {
        using var p = await PrepararPush(0, registrarAviso: false);
        var destino = await CrearDestino(p);
        await using var db = p.Data.CreateDbContext();
        var antes = await db.Envios.AsNoTracking().SingleAsync(e => e.Id == p.Aviso.EnvioId);
        using var respuesta = await p.Client.PostAsJsonAsync($"/api/envios/{antes.Id}/reasignacion", new
        {
            RutaDestinoId = destino.Id, VersionEnvio = antes.Version, VersionRutaDestino = destino.Version - 1,
            LlegadaEstimadaUtc = LlegadaEstimada.AddDays(1).AddHours(1), Motivo = "Otro momento"
        });
        Assert.Equal(HttpStatusCode.Conflict, respuesta.StatusCode);
        Assert.Equal(antes.Version, (await db.Envios.AsNoTracking().SingleAsync(e => e.Id == antes.Id)).Version);
        Assert.True((await db.ParadasRuta.SingleAsync(x => x.RutaId == p.Aviso.RutaId)).Activa);
        Assert.Empty(await db.AvisosReparto.ToArrayAsync());
        Assert.False(await db.OutboxMessages.AnyAsync(x => x.Destino == OutboxDestino.PushFcm));
    }

    private static async Task<HojaRutaResponse> CrearDestino(PreparadoPush p, bool vacia = false)
    {
        await using var db = p.Data.CreateDbContext();
        var comercio = await db.AccesosUsuario.SingleAsync(a => a.ComercioId != null);
        var actual = await db.AccesosUsuario.SingleAsync(a => a.Id == p.Aviso.AccesoId);
        var rol = await db.Roles.SingleAsync(r => r.Id == actual.RolId);
        var user = new Usuario("futuro-" + Guid.NewGuid().ToString("N") + "@example.test", AuthenticationTestData.Hash("prueba"));
        db.Usuarios.Add(user); await db.SaveChangesAsync();
        var acceso = new AccesoUsuario(user.Id, p.Aviso.OperadorId, null, rol);
        db.AccesosUsuario.Add(acceso); await db.SaveChangesAsync();
        db.Repartidores.Add(new Repartidor(acceso.Id, p.Aviso.OperadorId, "Repartidor futuro"));
        await db.SaveChangesAsync();
        await p.Client.SetBearerForAccessAsync(p.Data, comercio.Id);
        using var created = await p.Client.PostAsJsonAsync("/api/envios", new
        {
            OperadorId = p.Aviso.OperadorId, DestinatarioNombre = "Otro destino",
            DestinatarioEmail = "destinatario@example.test", Direccion = "Otra dirección de prueba",
            CodigoBulto = Guid.NewGuid().ToString("N"), PesoGramos = 100m,
            LargoCentimetros = 1m, AnchoCentimetros = 1m, AltoCentimetros = 1m
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var shipmentId = (await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();
        using var destinationAddress = await p.Client.PutAsJsonAsync($"/api/envios/{shipmentId}/destino",
            new { Version = 0, Latitud = -34.9, Longitud = -56.2 });
        Assert.Equal(HttpStatusCode.NoContent, destinationAddress.StatusCode);
        await p.Client.SetBearerForAccessAsync(p.Data, actual.Id);
        using var received = await p.Client.PostAsJsonAsync($"/api/deposito/envios/{shipmentId}/recepcion", new { Version = 0 });
        Assert.Equal(HttpStatusCode.NoContent, received.StatusCode);
        var vehiculo = await db.Vehiculos.SingleAsync();
        using var plan = await p.Client.PostAsJsonAsync("/api/rutas", new
        {
            RepartidorAccesoId = acceso.Id, VehiculoId = vehiculo.Id, Fecha = FechaOperacion.AddDays(1),
            Paradas = vacia ? [] : new[] { new { EnvioId = shipmentId, LlegadaEstimadaUtc = LlegadaEstimada.AddDays(1) } }
        });
        Assert.Equal(HttpStatusCode.OK, plan.StatusCode);
        return (await plan.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
    }

    private static async Task<ResultadoOperacionResponse> Operar(HttpClient client, HojaRutaResponse hoja, int secuencia,
        string tipo, Guid? envio = null, long? version = null, object? datos = null)
    {
        using var respuesta = await client.PostAsJsonAsync($"/api/mobile/jornadas/{hoja.JornadaId}/operaciones",
            new { Id = Guid.CreateVersion7(), Secuencia = secuencia, Tipo = tipo, EnvioId = envio, VersionEnvio = version,
                OcurridaEn = DateTimeOffset.UtcNow, Datos = datos ?? new { } });
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        return (await respuesta.Content.ReadFromJsonAsync<ResultadoOperacionResponse>())!;
    }
}
