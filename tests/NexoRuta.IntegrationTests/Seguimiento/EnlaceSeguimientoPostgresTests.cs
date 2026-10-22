using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Seguimiento.Services;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Seguimiento;
using NexoRuta.Infrastructure.Seguimiento.Consultas;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;
using Npgsql;

namespace NexoRuta.IntegrationTests.Seguimiento;

public sealed class EnlaceSeguimientoPostgresTests(PostgresFixture postgres)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private IntegrationDatabase database = null!;

    public async Task InitializeAsync()
    {
        database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        Assert.Empty(await db.Envios.AsNoTracking().ToListAsync());
        Assert.Empty(await db.EnlacesSeguimiento.AsNoTracking().ToListAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task TokenValido_Devuelve200()
    {
        string token;
        await using (var db = database.CreateDbContext())
        {
            var envio = await SeguimientoTestData.CreateEnvioAsync(db);
            token = await SeguimientoTestData.CreateActiveLinkAsync(db, envio);
        }

        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(database);
        using var api = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = api.CreateHttpClient();
        using var response = await client.GetAsync($"/api/public/seguimiento/{token}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "estado", "identidad" }, json.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal("Admitido", json.RootElement.GetProperty("estado").GetString());
        Assert.Equal("Integration operator", json.RootElement.GetProperty("identidad").GetProperty("nombre").GetString());
        Assert.False(json.RootElement.GetProperty("identidad").TryGetProperty("nombreAdministrativo", out _));
    }

    [Fact]
    public async Task TokenValido_ProyectaSoloLaMarcaPublicaDelOperadorDelEnvio()
    {
        string token;
        await using (var db = database.CreateDbContext())
        {
            var envio = await SeguimientoTestData.CreateEnvioAsync(db);
            db.VersionesIdentidadVisualOperador.Add(new NexoRuta.Domain.Administracion.VersionIdentidadVisualOperador(
                envio.OperadorId, 1, "Marca Sur", "hola@sur.test", "+598 99 123 456", "#AABBCC", null,
                null, null, envio.CreadoPorUsuarioId, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
            token = await SeguimientoTestData.CreateActiveLinkAsync(db, envio);
        }

        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(database);
        using var api = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = api.CreateHttpClient();
        using var response = await client.GetAsync($"/api/public/seguimiento/{token}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var identity = json.RootElement.GetProperty("identidad");
        Assert.Equal("Marca Sur", identity.GetProperty("nombre").GetString());
        Assert.Equal("hola@sur.test", identity.GetProperty("correoPublico").GetString());
        Assert.Equal("+598 99 123 456", identity.GetProperty("telefonoPublico").GetString());
        Assert.Equal("#AABBCC", identity.GetProperty("colorPrimario").GetString());
        Assert.Equal(new[] { "nombre", "correoPublico", "telefonoPublico", "colorPrimario", "colorSecundario", "logoContenido", "logoTipoContenido" },
            identity.EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public async Task TokenInexistente_Devuelve404() =>
        await AssertNotFoundAsync(GeneradorTokenSeguimiento.Generar());

    [Fact]
    public async Task TokenRevocado_Devuelve404()
    {
        string token;
        await using (var db = database.CreateDbContext())
        {
            var envio = await SeguimientoTestData.CreateEnvioAsync(db);
            token = await SeguimientoTestData.CreateRevokedLinkAsync(db, envio);
        }

        await using var verification = database.CreateDbContext();
        var enlace = await verification.EnlacesSeguimiento.AsNoTracking().SingleAsync();
        Assert.NotNull(enlace.RevocadoEnUtc);
        Assert.False(enlace.EstaVigente(DateTimeOffset.UtcNow));
        await AssertNotFoundAsync(token);
    }

    [Fact]
    public async Task TokenVencido_Devuelve404()
    {
        string token;
        await using (var db = database.CreateDbContext())
        {
            var envio = await SeguimientoTestData.CreateEnvioAsync(db);
            token = await SeguimientoTestData.CreateExpiredLinkAsync(db, envio);
        }

        await using var verification = database.CreateDbContext();
        var enlace = await verification.EnlacesSeguimiento.AsNoTracking().SingleAsync();
        Assert.Null(enlace.RevocadoEnUtc);
        Assert.False(enlace.EstaVigente(DateTimeOffset.UtcNow));
        await AssertNotFoundAsync(token);
    }

    [Fact]
    public async Task OperadorAjeno_NoPuedeConsultarEnvio()
    {
        Envio envio;
        Guid otherOperatorId;
        await using (var db = database.CreateDbContext())
        {
            envio = await SeguimientoTestData.CreateEnvioAsync(db);
            otherOperatorId = (await SeguimientoTestData.CreateOtherOperadorAsync(db)).Id;
        }

        await using var verification = database.CreateDbContext();
        var query = new EfConsultaEstadoEnvio(verification);
        Assert.Null(await query.ObtenerEstadoAsync(otherOperatorId, envio.Id));
        Assert.Equal(EstadoEnvio.Admitido,
            await query.ObtenerEstadoAsync(envio.OperadorId, envio.Id));
    }

    [Fact]
    public async Task EnlaceDeOtroOperador_ViolaClaveForanea()
    {
        EnlaceSeguimiento invalid;
        await using (var db = database.CreateDbContext())
        {
            var envio = await SeguimientoTestData.CreateEnvioAsync(db);
            var other = await SeguimientoTestData.CreateOtherOperadorAsync(db);
            var now = DateTimeOffset.UtcNow;
            invalid = new EnlaceSeguimiento(other.Id, envio.Id,
                GeneradorTokenSeguimiento.CalcularHash(GeneradorTokenSeguimiento.Generar()),
                now, now.AddHours(1));
        }

        await using (var db = database.CreateDbContext())
        {
            db.EnlacesSeguimiento.Add(invalid);
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            var postgresError = Assert.IsType<PostgresException>(error.InnerException);
            Assert.Equal("23503", postgresError.SqlState);
            Assert.Equal("FK_EnlacesSeguimiento_Envios_OperadorId_EnvioId", postgresError.ConstraintName);
        }

        await using var verification = database.CreateDbContext();
        Assert.False(await verification.EnlacesSeguimiento.AsNoTracking().AnyAsync(x => x.Id == invalid.Id));
    }

    [Fact]
    public async Task TokenValido_PersisteSoloHash()
    {
        string token;
        Envio envio;
        await using (var db = database.CreateDbContext())
        {
            envio = await SeguimientoTestData.CreateEnvioAsync(db);
            token = await SeguimientoTestData.CreateActiveLinkAsync(db, envio);
        }

        await using var verification = database.CreateDbContext();
        var persisted = await verification.EnlacesSeguimiento.AsNoTracking().SingleAsync();
        var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token)))
            .ToLowerInvariant();
        Assert.Equal(expectedHash, persisted.TokenHash);
        Assert.NotEqual(token, persisted.TokenHash);
        Assert.Equal(envio.Id, persisted.EnvioId);
        Assert.Equal(envio.OperadorId, persisted.OperadorId);

        // Inspect every stored column, not just EF's mapped hash property.
        var rows = await verification.Database.SqlQueryRaw<string>(
            """SELECT row_to_json(e)::text AS "Value" FROM "EnlacesSeguimiento" e""").ToListAsync();
        Assert.DoesNotContain(token, Assert.Single(rows));
    }

    private async Task AssertNotFoundAsync(string token)
    {
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(database);
        using var api = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = api.CreateHttpClient();
        using var response = await client.GetAsync($"/api/public/seguimiento/{token}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }
}
