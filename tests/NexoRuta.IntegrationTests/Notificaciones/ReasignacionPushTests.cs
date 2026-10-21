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
    public async Task ReasignacionRealNotificaSoloAlActualYExigeRetornoFisicoAntesDelNuevoDespacho()
    {
        using var p = await PrepararPush(1, registrarAviso: false);
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
            LlegadaEstimadaUtc = LlegadaEstimada.AddDays(1).AddHours(1), Motivo = "Postergar la entrega para mañana."
        });
        Assert.Equal(HttpStatusCode.OK, mover.StatusCode);
        var nueva = (await mover.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
        Assert.Single(nueva.Paradas);
        await using (var db = p.Data.CreateDbContext())
        {
            Assert.Equal(EstadoCustodiaBulto.Repartidor, (await db.Bultos.SingleAsync(b => b.Id == parada.Bultos[0].Id)).Custodia);
            Assert.Equal(EstadoEnvio.AsignadoRuta, (await db.Envios.SingleAsync(e => e.Id == parada.EnvioId)).Estado);
            var aviso = Assert.Single(await db.AvisosReparto.ToArrayAsync());
            Assert.Equal((p.Aviso.AccesoId, origen.Id, "Reasignacion"), (aviso.AccesoId, aviso.RutaId, aviso.Tipo));
            Assert.Equal(1, await db.OutboxMessages.CountAsync(m => m.Destino == OutboxDestino.PushFcm));
        }
        await using var services = await ServiciosWorker(p);
        var transporte = new Transporte();
        using var worker = new PushOutboxWorker(services.GetRequiredService<IServiceScopeFactory>(), transporte, NullLogger<PushOutboxWorker>.Instance);
        Assert.True(await worker.ProcesarSiguienteAsync(default));
        Assert.Equal(p.Fids, Assert.Single(transporte.Enviados).Fids);

        using var temprano = await p.Client.PostAsJsonAsync($"/api/rutas/{nueva.Id}/despacho", new { nueva.Version });
        Assert.Equal(HttpStatusCode.Conflict, temprano.StatusCode);
        Assert.Equal("Aplicada", (await Operar(p.Client, origen, 4, "Rendicion", datos: new { BultoIds = new[] { parada.Bultos[0].Id } })).Resultado);
        using var sinRecibir = await p.Client.PostAsJsonAsync($"/api/rutas/{nueva.Id}/despacho", new { nueva.Version });
        Assert.Equal(HttpStatusCode.Conflict, sinRecibir.StatusCode);
        await using (var db = p.Data.CreateDbContext())
        {
            var envio = await db.Envios.AsNoTracking().SingleAsync(e => e.Id == parada.EnvioId);
            using var recibir = await p.Client.PostAsJsonAsync($"/api/deposito/envios/{envio.Id}/recepcion", new { envio.Version });
            Assert.Equal(HttpStatusCode.NoContent, recibir.StatusCode);
        }
        using var final = await p.Client.PostAsJsonAsync($"/api/rutas/{nueva.Id}/despacho", new { nueva.Version });
        Assert.Equal(HttpStatusCode.OK, final.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EntregaFisicaOfflineRetiraAsignacionFuturaSinDecisionDeDespacho(bool destinoVacio)
    {
        using var p = await PrepararPush(1, registrarAviso: false);
        var origen = (await p.Client.GetFromJsonAsync<HojaRutaResponse>($"/api/mobile/rutas/{p.Aviso.RutaId}"))!;
        var parada = origen.Paradas[0];
        Assert.Equal("Aplicada", (await Operar(p.Client, origen, 1, "InicioJornada")).Resultado);
        Assert.Equal("Aplicada", (await Operar(p.Client, origen, 2, "CargarBulto", parada.EnvioId, parada.EnvioVersion, new { BultoId = parada.Bultos[0].Id })).Resultado);
        Assert.Equal("Aplicada", (await Operar(p.Client, origen, 3, "ConfirmarCarga")).Resultado);
        var cargada = (await p.Client.GetFromJsonAsync<HojaRutaResponse>($"/api/mobile/rutas/{origen.Id}"))!;
        var versionSinRed = cargada.Paradas[0].EnvioVersion;
        var destino = await CrearDestino(p, vacia: destinoVacio);
        using var mover = await p.Client.PostAsJsonAsync($"/api/envios/{parada.EnvioId}/reasignacion", new
        {
            RutaDestinoId = destino.Id, VersionEnvio = versionSinRed, VersionRutaDestino = destino.Version,
            LlegadaEstimadaUtc = LlegadaEstimada.AddDays(1).AddHours(1), Motivo = "Postergar para mañana."
        });
        Assert.Equal(HttpStatusCode.OK, mover.StatusCode);
        var nueva = (await mover.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
        var entrega = await Operar(p.Client, origen, 4, "EntregaFisica", parada.EnvioId, versionSinRed, new { Nombre = "Receptor sin conexión" });
        Assert.Equal("Aplicada", entrega.Resultado);
        Assert.Equal("EntregaPendienteValidacion", entrega.EstadosEnvio[parada.EnvioId]);
        await using (var db = p.Data.CreateDbContext())
        {
            var envio = await db.Envios.SingleAsync(e => e.Id == parada.EnvioId);
            Assert.NotNull(envio.EntregaFisicaEnUtc);
            Assert.Equal(EstadoCustodiaBulto.Destinatario, (await db.Bultos.SingleAsync(b => b.Id == parada.Bultos[0].Id)).Custodia);
            Assert.False((await db.ParadasRuta.SingleAsync(x => x.RutaId == nueva.Id && x.EnvioId == envio.Id)).Activa);
            var futura = await db.HojasRuta.SingleAsync(h => h.Id == nueva.Id);
            Assert.True(futura.Version > nueva.Version);
            using var obsoleto = await p.Client.PostAsJsonAsync($"/api/rutas/{futura.Id}/despacho", new { nueva.Version });
            Assert.Equal(HttpStatusCode.Conflict, obsoleto.StatusCode);
            using var vigente = await p.Client.PostAsJsonAsync($"/api/rutas/{futura.Id}/despacho", new { futura.Version });
            Assert.Equal(destinoVacio ? HttpStatusCode.Conflict : HttpStatusCode.OK, vigente.StatusCode);
        }
        if (!destinoVacio)
        {
            // Preparar el inicio de la jornada futura sin adelantar el reloj del resto de las pruebas.
            await using (var db = p.Data.CreateDbContext())
            {
                var futura = await db.HojasRuta.SingleAsync(h => h.Id == nueva.Id);
                futura.RegistrarInicio(DateTimeOffset.UtcNow); await db.SaveChangesAsync();
            }
            using var futuro = p.Factory.CreateHttpClient();
            await futuro.SetBearerForAccessAsync(p.Data, nueva.RepartidorAccesoId);
            Assert.Equal("Aplicada", (await Operar(futuro, nueva, 1, "FinJornada")).Resultado);
            await using var verificar = p.Data.CreateDbContext();
            Assert.Equal(EstadoEnvio.EntregaPendienteValidacion, (await verificar.Envios.SingleAsync(e => e.Id == parada.EnvioId)).Estado);
        }
        await using (var db = p.Data.CreateDbContext())
        {
            var id = (await db.AvisosReparto.SingleAsync()).Id;
            var detalle = (await p.Client.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/mobile/notificaciones/{id}"));
            Assert.False(detalle.GetProperty("vigente").GetBoolean());
        }
        await using var services = await ServiciosWorker(p);
        var transporte = new Transporte();
        using var worker = new PushOutboxWorker(services.GetRequiredService<IServiceScopeFactory>(), transporte, NullLogger<PushOutboxWorker>.Instance);
        Assert.True(await worker.ProcesarSiguienteAsync(default));
        Assert.Empty(transporte.Enviados);
        Assert.Equal("Aplicada", (await Operar(p.Client, origen, 5, "FinJornada")).Resultado);
        await using var final = p.Data.CreateDbContext();
        Assert.Equal(EstadoEnvio.EntregaSinValidacion, (await final.Envios.SingleAsync(e => e.Id == parada.EnvioId)).Estado);
        Assert.Contains("Receptor sin conexión", (await final.OperacionesJornada.SingleAsync(x => x.RutaId == origen.Id && x.Secuencia == 4)).Payload);
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
        var destinatario = new Destinatario(p.Aviso.OperadorId, "Otro destino", "destinatario@example.test");
        var direccion = new Direccion(p.Aviso.OperadorId, "Otra dirección de prueba");
        direccion.ConfirmarUbicacion(-34.9, -56.2, comercio.UsuarioId, DateTimeOffset.UtcNow, 0);
        var envio = new Envio(p.Aviso.OperadorId, comercio.ComercioId!.Value, comercio.UsuarioId, destinatario.Id, direccion.Id, p.Aviso.OperadorId);
        envio.CambiarEstado(EstadoEnvio.EnDeposito, 0);
        var bulto = new Bulto(p.Aviso.OperadorId, envio.Id, Guid.NewGuid().ToString("N"), 100, 1, 1, 1);
        bulto.RecibirEnDeposito();
        db.AddRange(destinatario, direccion, envio, bulto); await db.SaveChangesAsync();
        var vehiculo = await db.Vehiculos.SingleAsync();
        using var plan = await p.Client.PostAsJsonAsync("/api/rutas", new
        {
            RepartidorAccesoId = acceso.Id, VehiculoId = vehiculo.Id, Fecha = FechaOperacion.AddDays(1),
            Paradas = vacia ? [] : new[] { new { EnvioId = envio.Id, LlegadaEstimadaUtc = LlegadaEstimada.AddDays(1) } }
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
