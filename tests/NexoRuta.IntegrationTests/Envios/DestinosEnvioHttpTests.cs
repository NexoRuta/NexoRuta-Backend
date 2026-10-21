using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexoRuta.Application.Envios.Dtos;
using NexoRuta.Application.Envios.Interfaces;
using NexoRuta.Application.Envios.Exceptions;
using NexoRuta.Domain.Envios;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;
using NexoRuta.IntegrationTests.Seguimiento;

namespace NexoRuta.IntegrationTests.Envios;

public sealed class DestinosEnvioHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task PropuestasNoConfirmanYComercioConfirmaConRolSqlRestringido()
    {
        var data = await postgres.CreateDatabaseAsync(); using var seed = data.CreateApiFactory(); using (seed.CreateHttpClient()) { }
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        using var factory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        var geo = new GeocodificadorPrueba();
        using var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => { s.RemoveAll<IGeocodificador>(); s.AddSingleton<IGeocodificador>(geo); }));
        using var client = host.CreateClient(); await using var db = data.CreateDbContext();
        var comercio = await db.AccesosUsuario.SingleAsync(x => x.ComercioId != null);
        var op = await db.AccesosUsuario.SingleAsync(x => x.OperadorId != null);
        await client.SetBearerForAccessAsync(data, comercio.Id);
        using var alta = await client.PostAsJsonAsync("/api/envios", new { OperadorId = op.OperadorId,
            DestinatarioNombre = "Persona", DestinatarioEmail = "persona@example.test", Direccion = "Dirección de prueba 123",
            CodigoBulto = "DESTINO-1", PesoGramos = 100, LargoCentimetros = 10, AnchoCentimetros = 10, AltoCentimetros = 10 });
        Assert.Equal(HttpStatusCode.Created, alta.StatusCode);
        var envio = await db.Envios.SingleAsync(); var url = $"/api/envios/{envio.Id}/destino";
        var actual = await client.GetFromJsonAsync<DestinoEnvioDatos>(url);
        Assert.Null(actual!.Latitud); Assert.Null(actual.ConfirmadaEnUtc); Assert.Equal(0, actual.Version);
        using var propuesta = await client.PostAsync(url + "/propuestas", null); Assert.Equal(HttpStatusCode.OK, propuesta.StatusCode);
        var candidatas = (await propuesta.Content.ReadFromJsonAsync<PropuestasDestinoDatos>())!;
        Assert.Equal(2, candidatas.Propuestas.Count); Assert.Equal("Dirección de prueba 123", Assert.Single(geo.Consultas));
        Assert.Null((await db.Direcciones.AsNoTracking().SingleAsync()).ConfirmadaEnUtc);
        using var confirmar = await client.PutAsJsonAsync(url, new { actual.Version, Latitud = -34.901, Longitud = -56.201 });
        Assert.Equal(HttpStatusCode.NoContent, confirmar.StatusCode);
        var confirmada = (await client.GetFromJsonAsync<DestinoEnvioDatos>(url))!;
        Assert.Equal(1, confirmada.Version); Assert.Equal(-34.901, confirmada.Latitud); Assert.NotNull(confirmada.ConfirmadaEnUtc);
        using var repetir = await client.PutAsJsonAsync(url, new { Version = 0, Latitud = -34.1, Longitud = -56.1 }); Assert.Equal(HttpStatusCode.Conflict, repetir.StatusCode);
        var direccion = await db.Direcciones.AsNoTracking().SingleAsync(); Assert.Equal(comercio.UsuarioId, direccion.ConfirmadaPorUsuarioId);
        await client.SetBearerForAccessAsync(data, op.Id);
        using var recepcion = await client.PostAsJsonAsync($"/api/deposito/envios/{envio.Id}/recepcion", new { Version = 0 }); Assert.Equal(HttpStatusCode.NoContent, recepcion.StatusCode);
        await client.SetBearerForAccessAsync(data, comercio.Id);
        using var posterior = await client.PutAsJsonAsync(url, new { Version = 1, Latitud = -34.1, Longitud = -56.1 }); Assert.Equal(HttpStatusCode.Conflict, posterior.StatusCode);
        using var buscarPosterior = await client.PostAsync(url + "/propuestas", null); Assert.Equal(HttpStatusCode.Conflict, buscarPosterior.StatusCode);
        Assert.Single(geo.Consultas);
    }

    [Fact]
    public async Task OtraCuentaOUnOperadorNoConsultanNiConfirmanDestinoDelComercio()
    {
        var data = await postgres.CreateDatabaseAsync(); using var seed = data.CreateApiFactory(); using (seed.CreateHttpClient()) { }
        await using var db = data.CreateDbContext(); var extranjero = await SeguimientoTestData.CreateEnvioAsync(db);
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        using var factory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings()); using var client = factory.CreateHttpClient();
        var comercio = await db.AccesosUsuario.SingleAsync(x => x.ComercioId != null);
        await client.SetBearerForAccessAsync(data, comercio.Id); var url = $"/api/envios/{extranjero.Id}/destino";
        using var get = await client.GetAsync(url); Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        using var propuestas = await client.PostAsync(url + "/propuestas", null); Assert.Equal(HttpStatusCode.NotFound, propuestas.StatusCode);
        using var put = await client.PutAsJsonAsync(url, new { Version = 0, Latitud = -34.9, Longitud = -56.2 }); Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        await client.SetBearerForAccessAsync(data, (await db.AccesosUsuario.SingleAsync(x => x.OperadorId != null)).Id);
        using var operador = await client.GetAsync(url); Assert.Equal(HttpStatusCode.Forbidden, operador.StatusCode);
        Assert.Null((await db.Direcciones.SingleAsync()).ConfirmadaEnUtc);
    }

    [Fact]
    public async Task FaltaProveedorNoInventaCoordenadasYContratoRechazaCamposOmitidos()
    {
        var data = await postgres.CreateDatabaseAsync(); using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        await using var db = data.CreateDbContext();
        var comercio = await db.AccesosUsuario.SingleAsync(x => x.ComercioId != null);
        var op = await db.Operadores.SingleAsync();
        var persona = new Destinatario(op.Id, "Persona", "persona@example.test"); var direccion = new Direccion(op.Id, "Dirección de prueba");
        var envio = new Envio(op.Id, comercio.ComercioId!.Value, comercio.UsuarioId, persona.Id, direccion.Id, op.Id);
        db.AddRange(persona, direccion, envio); await db.SaveChangesAsync();
        await client.SetBearerForAccessAsync(data, comercio.Id); var url = $"/api/envios/{envio.Id}/destino";
        using var noProveedor = await client.PostAsync(url + "/propuestas", null); Assert.Equal(HttpStatusCode.ServiceUnavailable, noProveedor.StatusCode);
        foreach (var body in new object[] { new { Version = 0 }, new { Latitud = 0, Longitud = 0 }, new { Version = 0, Latitud = 91, Longitud = 0 }, new { Version = 0, Latitud = 0, Longitud = 0, OperadorId = op.Id } })
        {
            using var invalida = await client.PutAsJsonAsync(url, body); Assert.Equal(HttpStatusCode.BadRequest, invalida.StatusCode);
        }
        Assert.Null((await db.Direcciones.AsNoTracking().SingleAsync()).ConfirmadaEnUtc);
    }

    [Fact]
    public async Task DosConfirmacionesSimultaneasNoSobrescribenElPuntoConLaMismaVersion()
    {
        var data = await postgres.CreateDatabaseAsync(); using var seed = data.CreateApiFactory(); using (seed.CreateHttpClient()) { }
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        using var factory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings()); using var client = factory.CreateHttpClient();
        await using var db = data.CreateDbContext(); var comercio = await db.AccesosUsuario.SingleAsync(x => x.ComercioId != null); var op = await db.Operadores.SingleAsync();
        var persona = new Destinatario(op.Id, "Persona", "persona@example.test"); var dir = new Direccion(op.Id, "Dirección de prueba");
        var envio = new Envio(op.Id, comercio.ComercioId!.Value, comercio.UsuarioId, persona.Id, dir.Id, op.Id); db.AddRange(persona, dir, envio); await db.SaveChangesAsync();
        await client.SetBearerForAccessAsync(data, comercio.Id);
        var respuestas = await Task.WhenAll(client.PutAsJsonAsync($"/api/envios/{envio.Id}/destino", new { Version = 0, Latitud = -34.9, Longitud = -56.2 }),
            client.PutAsJsonAsync($"/api/envios/{envio.Id}/destino", new { Version = 0, Latitud = -34.8, Longitud = -56.1 }));
        Assert.Equal(new[] { HttpStatusCode.NoContent, HttpStatusCode.Conflict }, respuestas.Select(x => x.StatusCode).Order());
        foreach (var respuesta in respuestas) respuesta.Dispose();
        Assert.Equal(1, (await db.Direcciones.AsNoTracking().SingleAsync()).Version);
    }

    private sealed class GeocodificadorPrueba : IGeocodificador
    {
        public List<string> Consultas { get; } = [];
        public string Atribucion => "Proveedor controlado";
        public Task<IReadOnlyList<PropuestaDestinoDatos>> BuscarAsync(string direccion, CancellationToken ct)
        { Consultas.Add(direccion); return Task.FromResult<IReadOnlyList<PropuestaDestinoDatos>>([new("Primera", -34.9, -56.2), new("Segunda", -34.8, -56.1)]); }
    }
}
