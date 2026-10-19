using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.IntegrationTests.Fixtures;
using Npgsql;

namespace NexoRuta.IntegrationTests.Reparto;

public sealed class ConfiguracionRepartoHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task OperadorConfiguraExpresamenteYVersionNuevaNoSobrescribeLaAnterior()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = data.CreateDbContext();
        var acceso = await db.AccesosUsuario.SingleAsync(x => x.OperadorId != null);
        await client.SetBearerForAccessAsync(data, acceso.Id);
        using var ausente = await client.GetAsync("/api/reparto/configuracion");
        Assert.Equal(HttpStatusCode.NotFound, ausente.StatusCode);
        using var primera = await client.PutAsJsonAsync("/api/reparto/configuracion", Politica());
        Assert.Equal(HttpStatusCode.OK, primera.StatusCode);
        var inicial = (await primera.Content.ReadFromJsonAsync<ConfiguracionRepartoContract>())!;
        Assert.Equal(1, inicial.Version);
        Assert.Equal("America/Montevideo", inicial.ZonaHoraria);
        using var segunda = await client.PutAsJsonAsync("/api/reparto/configuracion", Politica(1, "UTC", false));
        Assert.Equal(HttpStatusCode.OK, segunda.StatusCode);
        Assert.Equal(2, (await segunda.Content.ReadFromJsonAsync<ConfiguracionRepartoContract>())!.Version);
        var historico = await db.ConfiguracionesReparto.AsNoTracking().Where(x => x.OperadorId == acceso.OperadorId).OrderBy(x => x.Version).ToArrayAsync();
        Assert.Equal(2, historico.Length);
        Assert.True(historico[0].RequiereFoto);
        Assert.False(historico[1].RequiereFoto);
        using var viejo = await client.PutAsJsonAsync("/api/reparto/configuracion", Politica());
        Assert.Equal(HttpStatusCode.Conflict, viejo.StatusCode);
        using var leida = await client.GetAsync("/api/reparto/configuracion");
        Assert.Equal(2, (await leida.Content.ReadFromJsonAsync<ConfiguracionRepartoContract>())!.Version);
    }

    [Theory]
    [InlineData("zona inexistente", "No hay nadie")]
    [InlineData("UTC", "")]
    public async Task ConfiguracionInvalidaNoCreaVersion(string zona, string motivo)
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = data.CreateDbContext();
        await client.SetBearerForAccessAsync(data, (await db.AccesosUsuario.SingleAsync(x => x.OperadorId != null)).Id);
        using var response = await client.PutAsJsonAsync("/api/reparto/configuracion", new
        { Version = 0, ZonaHoraria = zona, MaximoParadas = 20, MaximoIntentos = 3, Motivos = new[] { motivo } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await db.ConfiguracionesReparto.CountAsync());
    }

    [Fact]
    public async Task VehiculoTieneCapacidadesYControlDeVersionSinDuplicarMatricula()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = data.CreateDbContext();
        await client.SetBearerForAccessAsync(data, (await db.AccesosUsuario.SingleAsync(x => x.OperadorId != null)).Id);
        using var crear = await client.PutAsJsonAsync("/api/reparto/vehiculos", Vehiculo());
        Assert.Equal(HttpStatusCode.OK, crear.StatusCode);
        var vehiculo = (await crear.Content.ReadFromJsonAsync<VehiculoContract>())!;
        Assert.Equal("SAB1234", vehiculo.Matricula);
        using var repetido = await client.PutAsJsonAsync("/api/reparto/vehiculos", Vehiculo());
        Assert.Equal(HttpStatusCode.Conflict, repetido.StatusCode);
        using var actualizar = await client.PutAsJsonAsync("/api/reparto/vehiculos", Vehiculo(vehiculo.Id, 0, 750000));
        Assert.Equal(HttpStatusCode.OK, actualizar.StatusCode);
        Assert.Equal(1, (await actualizar.Content.ReadFromJsonAsync<VehiculoContract>())!.Version);
        using var viejo = await client.PutAsJsonAsync("/api/reparto/vehiculos", Vehiculo(vehiculo.Id));
        Assert.Equal(HttpStatusCode.Conflict, viejo.StatusCode);
        Assert.Single((await client.GetFromJsonAsync<VehiculoContract[]>("/api/reparto/vehiculos"))!);
    }

    [Fact]
    public async Task SqlNoPuedeAlterarLaPoliticaHistorica()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = data.CreateDbContext();
        await client.SetBearerForAccessAsync(data, (await db.AccesosUsuario.SingleAsync(x => x.OperadorId != null)).Id);
        using var crear = await client.PutAsJsonAsync("/api/reparto/configuracion", Politica());
        Assert.Equal(HttpStatusCode.OK, crear.StatusCode);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("UPDATE \"ConfiguracionesReparto\" SET \"MaximoIntentos\"=99"));
        Assert.Equal("55000", ex.SqlState);
        Assert.Equal(3, (await db.ConfiguracionesReparto.SingleAsync()).MaximoIntentos);
    }

    [Fact]
    public async Task ComercioNoPuedeConfigurarReglasDeRepartoNiVehiculos()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = data.CreateDbContext();
        await client.SetBearerForAccessAsync(data, (await db.AccesosUsuario.SingleAsync(x => x.ComercioId != null)).Id);
        using var politica = await client.PutAsJsonAsync("/api/reparto/configuracion", Politica());
        using var vehiculo = await client.PutAsJsonAsync("/api/reparto/vehiculos", Vehiculo());
        Assert.Equal(HttpStatusCode.Forbidden, politica.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, vehiculo.StatusCode);
    }

    private static object Politica(long version = 0, string zona = "America/Montevideo", bool foto = true) => new
    { Version = version, ZonaHoraria = zona, MaximoParadas = 30, MaximoIntentos = 3, RequiereFoto = foto,
        RequiereNombre = true, RequiereFirma = false, RequiereDocumento = false, RequiereFotoIntento = true,
        BloquearDiscrepanciasCarga = true, Motivos = new[] { "No hay nadie", "Dirección incorrecta" } };
    private static object Vehiculo(Guid id = default, long version = 0, decimal peso = 500000) => new
    { Id = id, Version = version, Matricula = " sab1234 ", PesoMaximoGramos = peso, VolumenMaximoCm3 = 4000000m, Activo = true };
}
