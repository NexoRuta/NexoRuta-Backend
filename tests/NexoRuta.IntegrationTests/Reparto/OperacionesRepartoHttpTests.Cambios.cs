using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.ReglasOperativas;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;
using static NexoRuta.IntegrationTests.Fixtures.RepartoHttpData;
namespace NexoRuta.IntegrationTests.Reparto;
public sealed partial class OperacionesRepartoHttpTests
{
    [Fact]
    public async Task RejectedPickupResolutionPreservesExistingMessageAndBusinessState()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var sheet = await Lista(data, client); var stop = sheet.Paradas.Single();
        await using var db = data.CreateDbContext();
        var before = await db.Bultos.AsNoTracking().SingleAsync(x => x.EnvioId == stop.EnvioId);
        var command = Operacion(Guid.NewGuid(), 1, "RetirarBultos", stop.EnvioId, stop.EnvioVersion,
            new { ResuelveOperacionId = Guid.NewGuid() });
        var ack = await Aplicar(client, sheet, command); Assert.Equal("Conflicto", ack.Resultado);
        var after = await db.Bultos.AsNoTracking().SingleAsync(x => x.Id == before.Id);
        Assert.Equal(before.Version, after.Version); Assert.Equal(before.Custodia, after.Custodia);
        Assert.Equal(JsonSerializer.Serialize(ack), JsonSerializer.Serialize(await Aplicar(client, sheet, command)));
        Assert.Equal("La resolución debe referenciar un retiro rechazado de esta misma visita.", ack.Detalle);
    }

    [Fact]
    public async Task DeliveryResolutionUsesNewLinkedCommandPreservesRejectedReceiptAndCannotDeliverAgain()
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var seed = data.CreateApiFactory()) using (seed.CreateHttpClient()) { }
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        var settings = RlsRestrictedHttpFlowTests.ApiSettings();
        settings["Reparto:EvidenciasEncryptionKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var factory = new IntegrationApiFactory(runtime, settings: settings); using var client = factory.CreateHttpClient();
        var ready = await Preparar(data, client); var sheet = await PlanYDespacho(client, ready);
        var stop = sheet.Paradas.Single(); var parcel = stop.Bultos.Single();
        Assert.Equal("Aplicada", (await Aplicar(client, sheet, Operacion(Guid.NewGuid(), 1, "InicioJornada"))).Resultado);
        var load = await Aplicar(client, sheet, Operacion(Guid.NewGuid(), 2, "CargarBulto", stop.EnvioId, stop.EnvioVersion, new { BultoId = parcel.Id }));
        Assert.Equal("Aplicada", load.Resultado);
        Assert.Equal("Aplicada", (await Aplicar(client, sheet, Operacion(Guid.NewGuid(), 3, "ConfirmarCarga"))).Resultado);
        var rejectedId = Guid.NewGuid();
        var rejected = await Aplicar(client, sheet, Operacion(rejectedId, 4, "EntregaBultos", stop.EnvioId, 0,
            new { ResultadosBultos = new[] { new { BultoId = parcel.Id, parcel.Version, Entregado = true } } }));
        Assert.Equal("Conflicto", rejected.Resultado);
        await using var db = data.CreateDbContext();
        var original = await db.OperacionesJornada.AsNoTracking().SingleAsync(o => o.Id == rejectedId);
        var foto = Guid.NewGuid();
        using var upload = await client.PutAsJsonAsync($"/api/mobile/jornadas/{sheet.JornadaId}/evidencias/{foto}",
            new { stop.EnvioId, Tipo = "Foto", Contenido = FotoPrueba });
        Assert.Equal(HttpStatusCode.NoContent, upload.StatusCode);
        object proof(Guid rejection) => new { ResuelveOperacionId = rejection, FotoId = foto, Nombre = "Receptor sintético",
            Latitud = -34.9, Longitud = -56.2, PrecisionMetros = 10, PosicionEnUtc = DateTimeOffset.UtcNow,
            ResultadosBultos = new[] { new { BultoId = parcel.Id, Version = load.VersionesBulto[parcel.Id], Entregado = true } } };
        Assert.Equal("Conflicto", (await Aplicar(client, sheet, Operacion(Guid.NewGuid(), 5, "EntregaBultos", stop.EnvioId, 0, proof(Guid.NewGuid())))).Resultado);
        var resolution = Operacion(Guid.NewGuid(), 6, "EntregaBultos", stop.EnvioId, 0, proof(rejectedId));
        var accepted = await Aplicar(client, sheet, resolution); Assert.Equal("Aplicada", accepted.Resultado);
        Assert.Equal(JsonSerializer.Serialize(accepted), JsonSerializer.Serialize(await Aplicar(client, sheet, resolution)));
        var retained = await db.OperacionesJornada.AsNoTracking().SingleAsync(o => o.Id == rejectedId);
        Assert.Equal(original.Payload, retained.Payload); Assert.Equal(original.Respuesta, retained.Respuesta); Assert.Equal(original.Hash, retained.Hash);
        var delivered = await db.Bultos.AsNoTracking().SingleAsync(b => b.Id == parcel.Id);
        Assert.Equal(ResultadoBulto.Entregado, delivered.Resultado); Assert.Equal(EstadoCustodiaBulto.Destinatario, delivered.Custodia);
        Assert.Equal("Conflicto", (await Aplicar(client, sheet, Operacion(Guid.NewGuid(), 7, "EntregaBultos", stop.EnvioId, 0, proof(rejectedId)))).Resultado);
        Assert.Equal("Aplicada", (await Aplicar(client, sheet, Operacion(Guid.NewGuid(), 8, "FinJornada"))).Resultado);
        Assert.Equal("Conflicto", (await Aplicar(client, sheet, Operacion(Guid.NewGuid(), 9, "EntregaBultos", stop.EnvioId, 0, proof(rejectedId)))).Resultado);
        Assert.Equal(delivered.Version, (await db.Bultos.AsNoTracking().SingleAsync(b => b.Id == parcel.Id)).Version);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task C31_EntregaParcialRespetaPoliticaCustodiaPruebaReintentosYConflictoPorBulto(bool permitida, bool devolver)
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var seed = data.CreateApiFactory()) using (seed.CreateHttpClient()) { }
        await PublicarPoliticaParcial(data, permitida, devolver ? PoliticaPendientesParcial.DevolverComercio : PoliticaPendientesParcial.NuevaVisitaSinRecargo);
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        var settings = RlsRestrictedHttpFlowTests.ApiSettings(); settings["Reparto:EvidenciasEncryptionKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var reloj = new ManualClock(DateTimeOffset.UtcNow);
        using var factory = new IntegrationApiFactory(runtime, settings: settings, timeProvider: reloj); using var client = factory.CreateHttpClient();
        var listo = await Preparar(data, client, bloquearDiscrepanciasCarga: false);
        await using (var db = data.CreateDbContext())
        {
            var envio = await db.Envios.SingleAsync(e => e.Id == listo.EnvioId);
            var otro = new Bulto(envio.OperadorId, envio.Id, "PARCIAL-DOS", 1000, 10, 10, 10); otro.RecibirEnDeposito(); db.Bultos.Add(otro); await db.SaveChangesAsync();
        }
        var hoja = await PlanYDespacho(client, listo);
        var p = hoja.Paradas.Single(); var primero = p.Bultos.First(); var segundo = p.Bultos.Last();
        Assert.Equal("Aplicada", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 1, "InicioJornada"))).Resultado);
        Assert.Equal("Aplicada", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 2, "CargarBulto", p.EnvioId, p.EnvioVersion, new { BultoId = primero.Id }))).Resultado);
        if (!permitida) Assert.Equal("Aplicada", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 3, "CargarBulto", p.EnvioId, p.EnvioVersion, new { BultoId = segundo.Id }))).Resultado);
        Assert.Equal("Aplicada", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), permitida ? 3 : 4, "ConfirmarCarga"))).Resultado);
        if (!permitida)
        {
            var r = await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 5, "EntregaBultos", p.EnvioId, p.EnvioVersion,
                new { ResultadosBultos = new[] { new { BultoId = primero.Id, Version = primero.Version + 1, Entregado = true, Motivo = (string?)null }, new { BultoId = segundo.Id, Version = segundo.Version + 1, Entregado = false, Motivo = (string?)"AUSENTE" } } }));
            Assert.Equal("Conflicto", r.Resultado); Assert.Contains("no admite", r.Detalle); return;
        }
        var foto = Guid.NewGuid();
        using var subida = await client.PutAsJsonAsync($"/api/mobile/jornadas/{hoja.JornadaId}/evidencias/{foto}", new { p.EnvioId, Tipo = "Foto", Contenido = FotoPrueba });
        Assert.Equal(HttpStatusCode.NoContent, subida.StatusCode);
        var comando = Operacion(Guid.NewGuid(), 4, "EntregaBultos", p.EnvioId, 0, // aggregate may be stale: parcel versions are authoritative
            new { FotoId = foto, Nombre = "Receptor sintético", Latitud = -34.9, Longitud = -56.2, PrecisionMetros = 10, PosicionEnUtc = DateTimeOffset.UtcNow,
                ResultadosBultos = new[] { new { BultoId = primero.Id, Version = primero.Version + 1, Entregado = true, Motivo = (string?)null }, new { BultoId = segundo.Id, segundo.Version, Entregado = false, Motivo = (string?)"AUSENTE" } } });
        var resultado = await Aplicar(client, hoja, comando); Assert.Equal("Aplicada", resultado.Resultado);
        Assert.Equal(JsonSerializer.Serialize(resultado), JsonSerializer.Serialize(await Aplicar(client, hoja, comando)));
        await using var verificar = data.CreateDbContext();
        var actual = await verificar.Envios.SingleAsync(e => e.Id == p.EnvioId); Assert.Equal(EstadoEnvio.EntregadoParcialmente, actual.Estado);
        var bultos = await verificar.Bultos.Where(b => b.EnvioId == actual.Id).ToArrayAsync();
        Assert.Equal(ResultadoBulto.Entregado, bultos.Single(b => b.Id == primero.Id).Resultado);
        Assert.Equal(EstadoCustodiaBulto.Deposito, bultos.Single(b => b.Id == segundo.Id).Custodia);
        var prueba = Assert.Single(await verificar.PruebasEntrega.Where(e => e.EnvioId == actual.Id).ToArrayAsync()); Assert.Equal(primero.Id, prueba.BultoId); Assert.Equal(foto, prueba.FotoId);
        Assert.Equal("Conflicto", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 5, "EntregaBultos", p.EnvioId, actual.Version,
            new { ResultadosBultos = new[] { new { BultoId = primero.Id, Version = primero.Version + 1, Entregado = true } } }))).Resultado);
        if (devolver)
        {
            using var devuelta = await client.PostAsJsonAsync($"/api/deposito/envios/{actual.Id}/devolucion-comercio", new { actual.Version });
            Assert.True(devuelta.IsSuccessStatusCode, await devuelta.Content.ReadAsStringAsync());
            var b = await verificar.Bultos.AsNoTracking().SingleAsync(b => b.Id == segundo.Id);
            Assert.Equal(ResultadoBulto.Devuelto, b.Resultado); Assert.Equal(EstadoCustodiaBulto.Comercio, b.Custodia);
        }
        else
        {
            using var nueva = await client.PostAsJsonAsync("/api/rutas", new { RepartidorAccesoId = listo.AccesoId, listo.VehiculoId,
                Fecha = FechaOperacion.AddDays(1), Paradas = new[] { new { p.EnvioId, LlegadaEstimadaUtc = LlegadaEstimada.AddDays(1) } } });
            Assert.True(nueva.IsSuccessStatusCode, await nueva.Content.ReadAsStringAsync());
            var siguiente = (await nueva.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
            var visita = siguiente.Paradas.Single();
            Assert.Equal(segundo.Id, Assert.Single(visita.Bultos).Id);
            Assert.Equal("Aplicada", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 6, "FinJornada"))).Resultado);
            var flota = await client.GetFromJsonAsync<VehiculoFlotaResponse[]>("/api/reparto/flota");
            var vehiculo = flota!.Single(v => v.Id == listo.VehiculoId); var ocupacion = vehiculo.Ocupaciones.Single(o => o.RutaId == hoja.Id);
            using var retorno = await client.PostAsJsonAsync($"/api/reparto/flota/{vehiculo.Id}/retorno", new { ComandoId = Guid.NewGuid(), OcupacionId = ocupacion.Id, ocupacion.Version, Motivo = "Retorno sintético tras entrega parcial." });
            Assert.True(retorno.IsSuccessStatusCode, await retorno.Content.ReadAsStringAsync());
            var vuelta = (await retorno.Content.ReadFromJsonAsync<ResultadoFlotaResponse>())!;
            using var liberar = await client.PostAsJsonAsync($"/api/reparto/flota/{vehiculo.Id}/liberar", new { ComandoId = Guid.NewGuid(), OcupacionId = ocupacion.Id, vuelta.Version, Motivo = "Inspección sintética sin carga." });
            Assert.True(liberar.IsSuccessStatusCode, await liberar.Content.ReadAsStringAsync());
            reloj.Set(LlegadaEstimada.AddDays(1));
            using var despacho = await client.PostAsJsonAsync($"/api/rutas/{siguiente.Id}/despacho", new { siguiente.Version });
            Assert.True(despacho.IsSuccessStatusCode, await despacho.Content.ReadAsStringAsync());
            Assert.Equal("Aplicada", (await Aplicar(client, siguiente, Operacion(Guid.NewGuid(), 1, "InicioJornada"))).Resultado);
            Assert.Equal("Aplicada", (await Aplicar(client, siguiente, Operacion(Guid.NewGuid(), 2, "CargarBulto", actual.Id, visita.EnvioVersion, new { BultoId = segundo.Id }))).Resultado);
            var salidaNueva = await Aplicar(client, siguiente, Operacion(Guid.NewGuid(), 3, "ConfirmarCarga")); Assert.Equal("Aplicada", salidaNueva.Resultado);
            var fotoIntento = Guid.NewGuid();
            using var subirIntento = await client.PutAsJsonAsync($"/api/mobile/jornadas/{siguiente.JornadaId}/evidencias/{fotoIntento}", new { EnvioId = actual.Id, Tipo = "Foto", Contenido = FotoPrueba });
            Assert.Equal(HttpStatusCode.NoContent, subirIntento.StatusCode);
            var intento = new { Id = Guid.NewGuid(), Secuencia = 4, Tipo = "IntentoFallido", EnvioId = actual.Id,
                VersionEnvio = salidaNueva.VersionesEnvio[actual.Id], OcurridaEn = LlegadaEstimada.AddDays(1).AddMinutes(2), Datos = new { Motivo = "AUSENTE", FotoId = fotoIntento } };
            var falloNuevo = await Aplicar(client, siguiente, intento); Assert.Equal("Aplicada", falloNuevo.Resultado);
            Assert.Equal(JsonSerializer.Serialize(falloNuevo), JsonSerializer.Serialize(await Aplicar(client, siguiente, intento)));
            Assert.Equal(EstadoEnvio.EntregadoParcialmente, (await verificar.Envios.AsNoTracking().SingleAsync(e => e.Id == actual.Id)).Estado);
            Assert.Equal(ResultadoBulto.Entregado, (await verificar.Bultos.AsNoTracking().SingleAsync(b => b.Id == primero.Id)).Resultado);
            Assert.Equal(2, (await verificar.ParadasRuta.AsNoTracking().SingleAsync(p => p.RutaId == hoja.Id)).BultoIds.Length);
        }
        var remitidos = await verificar.OutboxMessages.Where(m => m.EnvioId == actual.Id && m.VersionContrato == 2).ToArrayAsync(); Assert.NotEmpty(remitidos);
        // Durable worker ingress is idempotent and verifies the exact persisted event; commerce never reads another tenant's inbox.
        foreach (var aviso in remitidos)
            for (var i = 0; i < 2; i++) await verificar.Database.ExecuteSqlInterpolatedAsync($"SELECT nexoruta_app.receive_commerce_notice({aviso.Id},{aviso.Payload}::jsonb)");
        Assert.Equal(remitidos.Length, await verificar.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM public.\"AvisosComercio\" WHERE \"EnvioId\"={actual.Id}").SingleAsync());
    }
    private static async Task<HojaRutaResponse> PlanYDespacho(HttpClient client, Preparado p)
    {
        using var plan = await client.PostAsJsonAsync("/api/rutas", Solicitud(p));
        Assert.True(plan.IsSuccessStatusCode, await plan.Content.ReadAsStringAsync());
        var h = (await plan.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
        using var salida = await client.PostAsJsonAsync($"/api/rutas/{h.Id}/despacho", new { h.Version });
        Assert.True(salida.IsSuccessStatusCode, await salida.Content.ReadAsStringAsync()); return (await salida.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
    }
    private static async Task PublicarPoliticaParcial(IntegrationDatabase data, bool permitida, PoliticaPendientesParcial politica)
    {
        await using var db = data.CreateDbContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        var raiz = await db.ReglasOperativasOperadores.SingleAsync();
        var anterior = await db.VersionesReglasOperativas.Include(v => v.Motivos).Include(v => v.RequisitosPrueba).SingleAsync(v => v.Id == raiz.VersionVigenteId);
        var usuario = anterior.CreadaPorUsuarioId;
        var v = VersionReglasOperativas.CrearBorrador(raiz.OperadorId, usuario, anterior); db.Add(v); await db.SaveChangesAsync();
        v.ReemplazarConfiguracion(v.VersionConcurrencia, new(3, 1, PoliticaDevolucionAlAgotarIntentos.DecisionOperativa, 2880, 1440, 60,
            [TipoPruebaEntrega.Fotografia, TipoPruebaEntrega.NombreReceptor], [new("AUSENTE", "Ausente", true)]) { PermiteEntregaParcial = permitida, PoliticaPendientesParcial = politica });
        await db.SaveChangesAsync(); v.Publicar(v.VersionConcurrencia, raiz.UltimoNumeroPublicacion + 1, usuario); await db.SaveChangesAsync(); raiz.Publicar(v); await db.SaveChangesAsync(); await tx.CommitAsync();
    }
}
