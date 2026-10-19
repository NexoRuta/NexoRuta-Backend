using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.Domain.Envios;
using NexoRuta.IntegrationTests.Fixtures;
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
    public async Task EntregaFisicaPendienteSeConservaYAlCerrarPasaASinValidacion()
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
        Assert.Equal(EstadoEnvio.EntregaSinValidacion, (await final.Envios.SingleAsync(x => x.Id == parada.EnvioId)).Estado);
        Assert.Equal(5, (await final.HojasRuta.SingleAsync()).UltimaSecuencia);
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

    [Fact]
    public async Task CierreNoAdelantaSecuenciaNiPermiteIgnorarBultosSinRendir()
    {
        var data = await postgres.CreateDatabaseAsync(); using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var hoja = await Cargada(data, client);
        using var salto = await client.PostAsJsonAsync(Ruta(hoja), Operacion(Guid.NewGuid(), 5, "FinJornada"));
        Assert.Equal(HttpStatusCode.Conflict, salto.StatusCode);
        Assert.Equal("Conflicto", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 4, "FinJornada"))).Resultado);
        var ids = hoja.Paradas.SelectMany(x => x.Bultos).Select(x => x.Id).ToArray();
        Assert.Equal("Aplicada", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 5, "Rendicion", datos: new { BultoIds = ids }))).Resultado);
        Assert.Equal("Aplicada", (await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 6, "FinJornada"))).Resultado);
        var parada = hoja.Paradas[0];
        await using (var db = data.CreateDbContext())
        {
            var envio = await db.Envios.SingleAsync(x => x.Id == parada.EnvioId);
            Assert.Equal(EstadoEnvio.Reprogramado, envio.Estado);
            Assert.Equal(EstadoCustodiaBulto.PendienteDeposito, (await db.Bultos.SingleAsync(x => x.EnvioId == envio.Id)).Custodia);
            using var recibir = await client.PostAsJsonAsync($"/api/deposito/envios/{envio.Id}/recepcion", new { envio.Version });
            Assert.Equal(HttpStatusCode.NoContent, recibir.StatusCode);
        }
        await using var final = data.CreateDbContext(); Assert.Equal(EstadoCustodiaBulto.Deposito, (await final.Bultos.SingleAsync(x => x.EnvioId == parada.EnvioId)).Custodia);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EvidenciaCifradaEsInmutableYCompletaLaPruebaAutomaticamente(bool cancelacionConcurrente)
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = new IntegrationApiFactory(data, settings: new Dictionary<string, string?> { ["Reparto:EvidenciasEncryptionKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) });
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
        var prueba = await Aplicar(client, hoja, Operacion(Guid.NewGuid(), 5, "EvidenciaEntrega", parada.EnvioId, entrega.VersionEnvio,
            new { FotoId = fotoId, Nombre = "Receptor", Latitud = -34.9, Longitud = -56.2, PrecisionMetros = 10, PosicionEnUtc = DateTimeOffset.UtcNow }));
        Assert.Equal("Aplicada", prueba.Resultado);
        await using var db = data.CreateDbContext();
        Assert.Equal(EstadoEnvio.Entregado, (await db.Envios.SingleAsync(x => x.Id == parada.EnvioId)).Estado);
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
