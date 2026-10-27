using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.Domain.Envios;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;
using Npgsql;
using static NexoRuta.IntegrationTests.Fixtures.RepartoHttpData;
namespace NexoRuta.IntegrationTests.Reparto;

public sealed partial class OperacionesRepartoHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task ReintentoDevuelveLaMismaConfirmacionYSoloPersisteUnHecho()
    {
        var data = await postgres.CreateDatabaseAsync(); using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var hoja = await Lista(data, client); var id = Guid.CreateVersion7();
        var comando = Operacion(id, 1, "InicioJornada");
        using var primera = await client.PostAsJsonAsync(Ruta(hoja), comando);
        Assert.Equal(HttpStatusCode.OK, primera.StatusCode);
        var resultado = (await primera.Content.ReadFromJsonAsync<ResultadoOperacionResponse>())!;
        Assert.Equal("Aplicada", resultado.Resultado);
        using var repetida = await client.PostAsJsonAsync(Ruta(hoja), comando);
        Assert.Equal(HttpStatusCode.OK, repetida.StatusCode);
        var repetido = (await repetida.Content.ReadFromJsonAsync<ResultadoOperacionResponse>())!;
        Assert.Equal(JsonSerializer.Serialize(resultado), JsonSerializer.Serialize(repetido));
        using var adulterada = await client.PostAsJsonAsync(Ruta(hoja), Operacion(id, 2, "FinJornada"));
        Assert.Equal(HttpStatusCode.Conflict, adulterada.StatusCode);
        await using var db = data.CreateDbContext(); Assert.Single(await db.OperacionesJornada.ToArrayAsync());
        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE \"OperacionesJornada\" SET \"Resultado\"='Aplicada'"));
        Assert.Equal("55000", ex.SqlState);
    }

    [Fact]
    public async Task EntregaFisicaPendienteSeConservaTambienDespuesDeCerrarLaJornada()
    {
        var data = await postgres.CreateDatabaseAsync(); using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var hoja = await Cargada(data, client);
        var parada = hoja.Paradas[0];
        var entrega = await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 4, "EntregaFisica", parada.EnvioId, parada.EnvioVersion));
        Assert.Equal("Aplicada", entrega.Resultado);
        await using (var db = data.CreateDbContext())
        {
            var envio = await db.Envios.SingleAsync(x => x.Id == parada.EnvioId);
            Assert.Equal(EstadoEnvio.EntregaPendienteValidacion, envio.Estado); Assert.NotNull(envio.EntregaFisicaEnUtc);
            Assert.All(await db.Bultos.Where(x => x.EnvioId == envio.Id).ToArrayAsync(), b => Assert.Equal(EstadoCustodiaBulto.Destinatario, b.Custodia));
        }
        Assert.Equal("Aplicada", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 5, "FinJornada"))).Resultado);
        await using var final = data.CreateDbContext();
        Assert.Equal(EstadoEnvio.EntregaPendienteValidacion, (await final.Envios.SingleAsync(x => x.Id == parada.EnvioId)).Estado);
        Assert.Equal(5, (await final.HojasRuta.SingleAsync()).UltimaSecuencia);
        var cerrada = (await client.GetFromJsonAsync<HojaRutaResponse>($"/api/mobile/rutas/{hoja.Id}"))!;
        Assert.Empty(cerrada.IncidenciasCierre);
    }

    [Fact]
    public async Task CambioDeVersionConservaConflictoSinInventarEntregaNiModificarCustodia()
    {
        var data = await postgres.CreateDatabaseAsync(); using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var hoja = await Cargada(data, client); var parada = hoja.Paradas[0];
        var conflicto = await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 4, "EntregaFisica", parada.EnvioId, parada.EnvioVersion - 1));
        Assert.Equal("Conflicto", conflicto.Resultado);
        await using var db = data.CreateDbContext();
        var envio = await db.Envios.SingleAsync(x => x.Id == parada.EnvioId); Assert.Null(envio.EntregaFisicaEnUtc); Assert.Equal(EstadoEnvio.EnTransito, envio.Estado);
        Assert.Equal(EstadoCustodiaBulto.Repartidor, (await db.Bultos.SingleAsync(x => x.EnvioId == envio.Id)).Custodia);
        var hecho = await db.OperacionesJornada.SingleAsync(x => x.Secuencia == 4); Assert.Equal("Conflicto", hecho.Resultado);
        Assert.Contains("EntregaFisica", hecho.Payload);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CierreConIncidenciaEsIdempotenteConservaCustodiaYPermiteRecepcionFisicaPosterior(bool restringido)
    {
        var data = await postgres.CreateDatabaseAsync();
        using var owner = data.CreateApiFactory(); using var seed = owner.CreateHttpClient();
        var runtime = restringido ? await NexoRuta.IntegrationTests.Seguridad.RlsRestrictedHttpFlowTests.RestrictApiAsync(data) : data;
        using var factory = new IntegrationApiFactory(runtime,
            settings: NexoRuta.IntegrationTests.Seguridad.RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = factory.CreateHttpClient();
        var hoja = await Cargada(data, client); var parada = hoja.Paradas[0];
        using var salto = await client.PostAsJsonAsync(Ruta(hoja), Operacion(Guid.NewGuid(), 5, "FinJornada"));
        Assert.Equal(HttpStatusCode.Conflict, salto.StatusCode);
        using var prematura = await client.PostAsJsonAsync($"/api/deposito/envios/{parada.EnvioId}/recepcion", new { Version = parada.EnvioVersion });
        Assert.Equal(HttpStatusCode.Conflict, prematura.StatusCode);
        var comando = Operacion(Guid.NewGuid(), 4, "FinJornada");
        var cierre = await Aplicar(client, hoja, comando);
        Assert.Equal("Aplicada", cierre.Resultado); Assert.Contains("incidencia", cierre.Detalle);
        Assert.Equal(JsonSerializer.Serialize(cierre), JsonSerializer.Serialize(await Aplicar(client, hoja, comando)));
        var actual = (await client.GetFromJsonAsync<HojaRutaResponse>($"/api/mobile/rutas/{hoja.Id}"))!;
        var incidencia = Assert.Single(actual.IncidenciasCierre);
        Assert.Equal(parada.Bultos[0].Id, incidencia.BultoId);
        Assert.Equal(parada.EnvioId, incidencia.EnvioId);
        Assert.Equal("Finalizada", actual.Estado);
        await using (var db = data.CreateDbContext())
        {
            Assert.Equal(4, await db.OperacionesJornada.CountAsync());
            var envio = await db.Envios.SingleAsync(x => x.Id == parada.EnvioId);
            Assert.Equal(EstadoEnvio.EnTransito, envio.Estado); Assert.Null(envio.EntregaFisicaEnUtc);
            var bulto = await db.Bultos.SingleAsync(x => x.EnvioId == envio.Id);
            Assert.Equal(EstadoCustodiaBulto.Repartidor, bulto.Custodia); Assert.Equal(hoja.Id, bulto.RutaCustodiaId);
            Assert.Equal(hoja.RepartidorAccesoId, bulto.AccesoCustodioId);
            Assert.Equal(bulto.Version, incidencia.VersionBulto);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
                "UPDATE \"OperacionesJornada\" SET \"IncidenciasCierre\"='[]' WHERE \"Tipo\"='FinJornada'"));
            Assert.Equal("55000", ex.SqlState);
            using var recibir = await client.PostAsJsonAsync($"/api/deposito/envios/{envio.Id}/recepcion", new { envio.Version });
            Assert.Equal(HttpStatusCode.NoContent, recibir.StatusCode);
        }
        var recibida = (await client.GetFromJsonAsync<HojaRutaResponse>($"/api/mobile/rutas/{hoja.Id}"))!;
        Assert.Equal(incidencia, Assert.Single(recibida.IncidenciasCierre));
        Assert.Equal("Deposito", recibida.Paradas[0].Bultos[0].Custodia);
        Assert.Equal("Reprogramado", recibida.Paradas[0].Estado);
        Assert.Equal("Conflicto", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 5, "EntregaFisica", parada.EnvioId, parada.EnvioVersion))).Resultado);
    }

    [Fact]
    public async Task RendicionDeclaradaAntesDelCierreNoGeneraIncidencias()
    {
        var data = await postgres.CreateDatabaseAsync(); using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var hoja = await Cargada(data, client);
        var ids = hoja.Paradas.SelectMany(x => x.Bultos).Select(x => x.Id).ToArray();
        Assert.Equal("Aplicada", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 4, "Rendicion", datos: new { BultoIds = ids }))).Resultado);
        Assert.Equal("Aplicada", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 5, "FinJornada"))).Resultado);
        var actual = (await client.GetFromJsonAsync<HojaRutaResponse>($"/api/mobile/rutas/{hoja.Id}"))!;
        Assert.Empty(actual.IncidenciasCierre);
        Assert.Equal("PendienteDeposito", actual.Paradas[0].Bultos[0].Custodia);
    }

    [Fact]
    public async Task CierreDuranteLaCargaNoFingeQueElBultoCargadoEstaEnDeposito()
    {
        var data = await postgres.CreateDatabaseAsync(); using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var hoja = await Lista(data, client); var parada = hoja.Paradas[0];
        await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 1, "InicioJornada"));
        await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 2, "CargarBulto", parada.EnvioId, parada.EnvioVersion, new { BultoId = parada.Bultos[0].Id }));
        Assert.Equal("Aplicada", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 3, "FinJornada"))).Resultado);
        var cerrada = (await client.GetFromJsonAsync<HojaRutaResponse>($"/api/mobile/rutas/{hoja.Id}"))!;
        Assert.Single(cerrada.IncidenciasCierre);
        Assert.Equal("AsignadoRuta", cerrada.Paradas[0].Estado);
        Assert.Equal("Repartidor", cerrada.Paradas[0].Bultos[0].Custodia);
        using var recibir = await client.PostAsJsonAsync($"/api/deposito/envios/{parada.EnvioId}/recepcion", new { Version = cerrada.Paradas[0].EnvioVersion });
        Assert.Equal(HttpStatusCode.NoContent, recibir.StatusCode);
        var recibida = (await client.GetFromJsonAsync<HojaRutaResponse>($"/api/mobile/rutas/{hoja.Id}"))!;
        Assert.Single(recibida.IncidenciasCierre);
        Assert.Equal("EnDeposito", recibida.Paradas[0].Estado);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EvidenciaCifradaEsInmutableYCompletaLaPruebaAutomaticamente(bool cancelacionConcurrente, bool restringida)
    {
        var data = await postgres.CreateDatabaseAsync();
        var settings = restringida ? RlsRestrictedHttpFlowTests.ApiSettings() : new Dictionary<string, string?>();
        if (restringida)
        {
            using var bootstrap = data.CreateApiFactory();
            using var bootstrapClient = bootstrap.CreateHttpClient();
        }
        var runtime = restringida ? await RlsRestrictedHttpFlowTests.RestrictApiAsync(data) : data;
        settings["Reparto:EvidenciasEncryptionKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var factory = new IntegrationApiFactory(runtime, settings: settings);
        using var client = factory.CreateHttpClient(); var hoja = await Cargada(data, client); var parada = hoja.Paradas[0];
        if (cancelacionConcurrente)
        {
            await using var cancelacion = data.CreateDbContext();
            var envio = await cancelacion.Envios.SingleAsync(e => e.Id == parada.EnvioId);
            envio.CambiarEstado(EstadoEnvio.Cancelado, envio.Version);
            await cancelacion.SaveChangesAsync();
        }
        var entrega = await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 4, "EntregaFisica", parada.EnvioId, parada.EnvioVersion));
        var fotoId = Guid.NewGuid(); var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==");
        var uri = $"/api/mobile/jornadas/{hoja.JornadaId}/evidencias/{fotoId}";
        var evidencia = new { parada.EnvioId, Tipo = "Foto", Contenido = bytes };
        using var subir = await client.PutAsJsonAsync(uri, evidencia); Assert.Equal(HttpStatusCode.NoContent, subir.StatusCode);
        using var reintentar = await client.PutAsJsonAsync(uri, evidencia); Assert.Equal(HttpStatusCode.NoContent, reintentar.StatusCode);
        using var reemplazar = await client.PutAsJsonAsync(uri, new { parada.EnvioId, Tipo = "Firma", Contenido = bytes }); Assert.Equal(HttpStatusCode.Conflict, reemplazar.StatusCode);
        var comandoPrueba = Operacion(Guid.NewGuid(), 5, "EvidenciaEntrega", parada.EnvioId, entrega.VersionEnvio,
            new { FotoId = fotoId, Nombre = "Receptor", Latitud = -34.9, Longitud = -56.2, PrecisionMetros = 10, PosicionEnUtc = DateTimeOffset.UtcNow });
        var prueba = await Aplicar(client, hoja, comandoPrueba);
        Assert.Equal("Aplicada", prueba.Resultado);
        Assert.Equal(JsonSerializer.Serialize(prueba), JsonSerializer.Serialize(await Aplicar(client, hoja, comandoPrueba)));
        await using var db = data.CreateDbContext();
        var envioFinal = await db.Envios.SingleAsync(x => x.Id == parada.EnvioId);
        Assert.Equal(cancelacionConcurrente ? EstadoEnvio.Cancelado : EstadoEnvio.Entregado, envioFinal.Estado);
        Assert.Equal(entrega.VersionEnvio + 1, envioFinal.Version);
        Assert.Equal(LlegadaEstimada, envioFinal.EntregaFisicaEnUtc);
        Assert.Equal(cancelacionConcurrente ? "Cancelado" : "Entregado", prueba.EstadosEnvio[parada.EnvioId]);
        Assert.Single(await db.OperacionesJornada.Where(x => x.Id == prueba.Id).ToArrayAsync());
        var pruebaGuardada = await db.PruebasEntrega.SingleAsync(x => x.EnvioId == parada.EnvioId);
        Assert.Equal(fotoId, pruebaGuardada.FotoId);
        Assert.Equal("Receptor", pruebaGuardada.Nombre);
        Assert.Equal(-34.9, pruebaGuardada.Latitud);
        Assert.Equal(EstadoCustodiaBulto.Destinatario, (await db.Bultos.SingleAsync(x => x.Id == parada.Bultos[0].Id)).Custodia);
        var archivo = Assert.Single(await db.EvidenciasReparto.ToArrayAsync()); Assert.False(bytes.SequenceEqual(archivo.Cifrado));
        Assert.Equal(64, archivo.Hash.Length); Assert.Equal(12, archivo.Nonce.Length); Assert.Equal(16, archivo.Tag.Length);
    }

    [Fact]
    public async Task CargaNoAdmiteBultosAjenosNiOtraCargaLuegoDeSalir()
    {
        var data = await postgres.CreateDatabaseAsync(); using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var hoja = await Cargada(data, client); var parada = hoja.Paradas[0];
        var carga = await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 4, "CargarBulto", parada.EnvioId, parada.EnvioVersion, new { BultoId = Guid.NewGuid() }));
        Assert.Equal("Conflicto", carga.Resultado);
        Assert.Equal("Conflicto", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 5, "ConfirmarCarga"))).Resultado);
    }

    private static async Task<HojaRutaResponse> Lista(IntegrationDatabase data, HttpClient client)
    {
        var listo = await Preparar(data, client);
        using var plan = await client.PostAsJsonAsync("/api/rutas", Solicitud(listo)); Assert.Equal(HttpStatusCode.OK, plan.StatusCode);
        var hoja = (await plan.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
        using var despacho = await client.PostAsJsonAsync($"/api/rutas/{hoja.Id}/despacho", new { hoja.Version }); Assert.Equal(HttpStatusCode.OK, despacho.StatusCode);
        return (await despacho.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
    }
    private static async Task<HojaRutaResponse> Cargada(IntegrationDatabase data, HttpClient client)
    {
        var hoja = await Lista(data, client); var parada = hoja.Paradas[0];
        Assert.Equal("Aplicada", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 1, "InicioJornada"))).Resultado);
        Assert.Equal("Aplicada", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 2, "CargarBulto", parada.EnvioId, parada.EnvioVersion, new { BultoId = parada.Bultos[0].Id }))).Resultado);
        Assert.Equal("Aplicada", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 3, "ConfirmarCarga"))).Resultado);
        return Assert.Single((await client.GetFromJsonAsync<HojaRutaResponse[]>($"/api/mobile/rutas?fecha={FechaOperacion:yyyy-MM-dd}"))!);
    }
    private static object Operacion(Guid id, long secuencia, string tipo, Guid? envio = null, long? version = null, object? datos = null) =>
        new { Id = id, Secuencia = secuencia, Tipo = tipo, EnvioId = envio, VersionEnvio = version,
            OcurridaEn = LlegadaEstimada, Datos = datos ?? new { } };
    private static string Ruta(HojaRutaResponse hoja) => $"/api/mobile/jornadas/{hoja.JornadaId}/operaciones";
    private static async Task<ResultadoOperacionResponse> Aplicar(HttpClient client, HojaRutaResponse hoja, object comando)
    {
        using var resultado = await client.PostAsJsonAsync(Ruta(hoja), comando); Assert.Equal(HttpStatusCode.OK, resultado.StatusCode);
        return (await resultado.Content.ReadFromJsonAsync<ResultadoOperacionResponse>())!;
    }
}
