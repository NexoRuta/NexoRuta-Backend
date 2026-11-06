using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
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
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task H1H2_SegundaVisitaPersisteYPlazosDeBultosSeResuelvenIndependientemente(bool primeraCompleta, bool completarSegunda)
    {
        var data = await postgres.CreateDatabaseAsync("20261105063923_SnapshotParcelsPerVisit");
        await using (var actualizar = data.CreateDbContext()) await actualizar.Database.MigrateAsync();
        using (var seed = data.CreateApiFactory()) using (seed.CreateHttpClient()) { }
        await PublicarPoliticaParcial(data, true, PoliticaPendientesParcial.NuevaVisitaSinRecargo);
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        var settings = RlsRestrictedHttpFlowTests.ApiSettings(); settings["Reparto:EvidenciasEncryptionKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var reloj = new ManualClock(DateTimeOffset.UtcNow);
        settings["RoutingGeoapify:PermitirLlamadasReales"] = "true"; settings["RoutingGeoapify:ApiKey"] = "SYNTHETIC-NOT-A-CREDENTIAL";
        using var factory = new IntegrationApiFactory(runtime, settings: settings, timeProvider: reloj);
        using var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddHttpClient("RoutingGeoapify").ConfigurePrimaryHttpMessageHandler(() => new RoutingBultosControlado())));
        using var client = host.CreateClient();
        var listo = await Preparar(data, client, bloquearDiscrepanciasCarga: false);
        await using (var db = data.CreateDbContext())
        {
            var envio = await db.Envios.SingleAsync(e => e.Id == listo.EnvioId);
            var otro = new Bulto(envio.OperadorId, envio.Id, "PARCIAL-DOS", 1000, 10, 10, 10); otro.RecibirEnDeposito(); db.Bultos.Add(otro); await db.SaveChangesAsync();
        }
        await ConfigurarBaseVial(client, listo.VehiculoId);
        var hoja = await PlanYDespacho(client, listo);
        var p = hoja.Paradas.Single(); var primero = p.Bultos.First(); var segundo = p.Bultos.Last();
        Assert.Equal("Aplicada", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 1, "InicioJornada"))).Resultado);
        Assert.Equal("Aplicada", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 2, "CargarBulto", p.EnvioId, p.EnvioVersion, new { BultoId = primero.Id }))).Resultado);
        Assert.Equal("Aplicada", (await Aplicar(client, hoja, new { Id = Guid.NewGuid(), Secuencia = 3, Tipo = "ConfirmarCarga", OcurridaEn = reloj.GetUtcNow(), Datos = new { } })).Resultado);
        var t0 = new DateTimeOffset(reloj.GetUtcNow().Ticks / 10 * 10, TimeSpan.Zero); reloj.Set(t0);
        var foto = Guid.NewGuid();
        using var subida = await client.PutAsJsonAsync($"/api/mobile/jornadas/{hoja.JornadaId}/evidencias/{foto}", new { p.EnvioId, Tipo = "Foto", Contenido = FotoPrueba });
        Assert.Equal(HttpStatusCode.NoContent, subida.StatusCode);
        if (primeraCompleta)
        {
            var invalida = await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 4, "EntregaBultos", p.EnvioId, 0,
                new { ResultadosBultos = new[] {
                    new { BultoId = primero.Id, Version = primero.Version + 1, Entregado = true, Motivo = (string?)null },
                    new { BultoId = segundo.Id, segundo.Version, Entregado = false, Motivo = (string?)"MOTIVO_INEXISTENTE" } } }));
            Assert.Equal("Conflicto", invalida.Resultado);
            await using var intacta = data.CreateDbContext();
            Assert.False(await intacta.PruebasEntrega.AnyAsync(x => x.EnvioId == p.EnvioId));
            Assert.All(await intacta.Bultos.Where(x => x.EnvioId == p.EnvioId).ToArrayAsync(), b => Assert.Equal(ResultadoBulto.Pendiente, b.Resultado));
        }
        var primera = await Aplicar(client, hoja, Operacion(Guid.NewGuid(), primeraCompleta ? 5 : 4, "EntregaBultos", p.EnvioId, 0,
            new { FotoId = primeraCompleta ? (Guid?)foto : null, Nombre = "Receptor", Latitud = -34.9, Longitud = -56.2, PrecisionMetros = 10, PosicionEnUtc = t0, ResultadosBultos = new[] {
                new { BultoId = primero.Id, Version = primero.Version + 1, Entregado = true, Motivo = (string?)null },
                new { BultoId = segundo.Id, segundo.Version, Entregado = false, Motivo = (string?)"AUSENTE" } } }));
        Assert.Equal("Aplicada", primera.Resultado);
        await using var verificar = data.CreateDbContext();
        var actual = await verificar.Envios.AsNoTracking().SingleAsync(e => e.Id == p.EnvioId);
        var fisica = actual.EntregaFisicaEnUtc; var recibida = actual.EntregaFisicaRecibidaEnUtc; var plazo = actual.PlazoPruebaVenceEnUtc;
        // A second courier can revisit while the first one's proof remains pending.
        Guid segundoAcceso;
        await using (var seed = data.CreateDbContext())
        {
            var acceso = await seed.AccesosUsuario.SingleAsync(x => x.Id == listo.AccesoId);
            var usuario = new NexoRuta.Domain.Administracion.Usuario($"revisita-{Guid.NewGuid():N}@example.test", (await seed.Usuarios.SingleAsync(u => u.Id == acceso.UsuarioId)).PasswordHash!);
            seed.Add(usuario); await seed.SaveChangesAsync();
            var rol = await seed.Roles.SingleAsync(x => x.Id == acceso.RolId);
            var otro = new NexoRuta.Domain.Administracion.AccesoUsuario(usuario.Id, acceso.OperadorId, null, rol);
            seed.Add(otro); await seed.SaveChangesAsync(); segundoAcceso = otro.Id;
            seed.Add(new NexoRuta.Domain.Reparto.Repartidor(otro.Id, acceso.OperadorId!.Value, "Segundo repartidor sintético")); await seed.SaveChangesAsync();
        }
        using var crearVehiculo = await client.PutAsJsonAsync("/api/reparto/vehiculos", new { Matricula = "REV-TEST", PesoMaximoGramos = 100000m, VolumenMaximoCm3 = 100000m, Activo = true });
        Assert.Equal(HttpStatusCode.OK, crearVehiculo.StatusCode);
        var segundoVehiculo = (await crearVehiculo.Content.ReadFromJsonAsync<VehiculoContract>())!.Id;
        using var inspeccion = await client.PostAsJsonAsync($"/api/reparto/flota/{segundoVehiculo}/verificar-inicial", new { ComandoId = Guid.NewGuid(), Version = 0, Motivo = "Inspección sintética sin carga." });
        Assert.True(inspeccion.IsSuccessStatusCode, await inspeccion.Content.ReadAsStringAsync());
        await ConfigurarPerfilVial(client, segundoVehiculo);
        using var nueva = await client.PostAsJsonAsync("/api/rutas", new { RepartidorAccesoId = segundoAcceso, VehiculoId = segundoVehiculo,
            Fecha = FechaOperacion, Paradas = new[] { new { p.EnvioId, LlegadaEstimadaUtc = LlegadaEstimada.AddMinutes(20) } } });
        Assert.True(nueva.IsSuccessStatusCode, await nueva.Content.ReadAsStringAsync());
        var siguiente = (await nueva.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
        var visita = siguiente.Paradas.Single(); Assert.Equal(segundo.Id, Assert.Single(visita.Bultos).Id);
            await client.SetBearerForAccessAsync(data, segundoAcceso);
            reloj.Set(t0.AddMinutes(20));
            using var despacho = await client.PostAsJsonAsync($"/api/rutas/{siguiente.Id}/despacho", new { siguiente.Version });
            Assert.True(despacho.IsSuccessStatusCode, await despacho.Content.ReadAsStringAsync());
            Assert.Equal("Aplicada", (await Aplicar(client, siguiente, Operacion(Guid.NewGuid(), 1, "InicioJornada"))).Resultado);
            Assert.Equal("Aplicada", (await Aplicar(client, siguiente, Operacion(Guid.NewGuid(), 2, "CargarBulto", actual.Id, visita.EnvioVersion, new { BultoId = segundo.Id }))).Resultado);
            var salidaNueva = await Aplicar(client, siguiente, new { Id = Guid.NewGuid(), Secuencia = 3, Tipo = "ConfirmarCarga", OcurridaEn = reloj.GetUtcNow(), Datos = new { } }); Assert.Equal("Aplicada", salidaNueva.Resultado);
        await VerificarRecorridoBultos(data, client, siguiente, reloj.GetUtcNow(), 4, actual.Id);
        var entregaSegunda = new { Id = Guid.NewGuid(), Secuencia = 5, Tipo = "EntregaBultos", EnvioId = actual.Id, VersionEnvio = 0, OcurridaEn = t0.AddMinutes(20), Datos =
            new { Nombre = "Segundo receptor", Latitud = -34.9, Longitud = -56.2, PrecisionMetros = 10, PosicionEnUtc = t0, ResultadosBultos = new[] { new { BultoId = segundo.Id, Version = visita.Bultos.Single().Version + 1, Entregado = true } } } };
        var resultado = await Aplicar(client, siguiente, entregaSegunda);
        Assert.True(resultado.Resultado == "Aplicada", resultado.Detalle);
        Assert.Equal(JsonSerializer.Serialize(resultado), JsonSerializer.Serialize(await Aplicar(client, siguiente, entregaSegunda)));
        actual = await verificar.Envios.AsNoTracking().SingleAsync(e => e.Id == actual.Id);
        Assert.Equal(fisica, actual.EntregaFisicaEnUtc); Assert.Equal(recibida, actual.EntregaFisicaRecibidaEnUtc); Assert.Equal(plazo, actual.PlazoPruebaVenceEnUtc);
        var b2 = await verificar.Bultos.AsNoTracking().SingleAsync(b => b.Id == segundo.Id);
        Assert.Equal(ResultadoBulto.EntregaPendienteValidacion, b2.Resultado); Assert.Equal(t0.AddMinutes(80), b2.PruebaVenceEnUtc);
        await VerificarRecorridoBultos(data, client, siguiente, reloj.GetUtcNow(), 6);
        if (primeraCompleta) return;
        Assert.Equal(1, (await Task.WhenAll(Expire(data.ConnectionString, t0.AddMinutes(60)), Expire(data.ConnectionString, t0.AddMinutes(60)))).Sum());
        Assert.Equal(ResultadoBulto.EntregaSinValidacion, (await verificar.Bultos.AsNoTracking().SingleAsync(b => b.Id == primero.Id)).Resultado);
        Assert.Equal(ResultadoBulto.EntregaPendienteValidacion, (await verificar.Bultos.AsNoTracking().SingleAsync(b => b.Id == segundo.Id)).Resultado);
        if (completarSegunda)
        {
            reloj.Set(t0.AddMinutes(70));
            var foto2 = Guid.NewGuid();
            using var subir = await client.PutAsJsonAsync($"/api/mobile/jornadas/{siguiente.JornadaId}/evidencias/{foto2}", new { EnvioId = actual.Id, Tipo = "Foto", Contenido = FotoPrueba });
            Assert.Equal(HttpStatusCode.NoContent, subir.StatusCode);
            var evidencia = await Aplicar(client, siguiente, Operacion(Guid.NewGuid(), 7, "EntregaBultos", actual.Id, 0,
                new { FotoId = foto2, Nombre = "Segundo receptor", Latitud = -34.9, Longitud = -56.2, PrecisionMetros = 10, PosicionEnUtc = t0, ResultadosBultos = new[] { new { BultoId = segundo.Id, b2.Version, Entregado = true } } }));
            Assert.Equal("Aplicada", evidencia.Resultado);
        }
        else Assert.Equal(1, await Expire(data.ConnectionString, t0.AddMinutes(80)));
        Assert.Equal(completarSegunda ? ResultadoBulto.Entregado : ResultadoBulto.EntregaSinValidacion, (await verificar.Bultos.AsNoTracking().SingleAsync(b => b.Id == segundo.Id)).Resultado);
        Assert.Equal(ResultadoBulto.EntregaSinValidacion, (await verificar.Bultos.AsNoTracking().SingleAsync(b => b.Id == primero.Id)).Resultado);
        Assert.Equal(EstadoEnvio.EntregaSinValidacion, (await verificar.Envios.AsNoTracking().SingleAsync(e => e.Id == actual.Id)).Estado);
        Assert.Equal(0, await Expire(data.ConnectionString, t0.AddMinutes(100)));
    }
}
