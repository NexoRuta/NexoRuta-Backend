using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.IdentityModel.Tokens;
using NexoRuta.Api.Core.Security;
using NexoRuta.Api.Features.Autenticacion.Contracts;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Administracion.Autenticacion;
using NexoRuta.IntegrationTests.Fixtures;

namespace NexoRuta.IntegrationTests.Seguridad;

public sealed class SesionesMovilesHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Ruta = "/api/auth/mobile/";

    [Fact]
    public async Task LoginCentralSeConservaYMobileGuardaSoloHashesConTreintaDiasAbsolutos()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var (user, acceso) = await Operador(db);
        var hashAnterior = user.PasswordHash;
        using var web = await client.PostAsJsonAsync("/api/auth/login", new { user.Email, Password = AuthenticationTestData.Password });
        Assert.Equal(HttpStatusCode.OK, web.StatusCode);
        var documento = await web.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "accessToken", "contexto", "expiresAtUtc", "tipoOrganizacion", "tokenType" },
            documento.EnumerateObject().Select(x => x.Name).Order());
        var jwtWeb = documento.GetProperty("accessToken").GetString()!;
        Assert.DoesNotContain(new JwtSecurityTokenHandler().ReadJwtToken(jwtWeb).Claims, x => x.Type == JwtSesionClaims.SesionMovil);
        var antes = DateTimeOffset.UtcNow;
        using var response = await LoginResponse(client, user.Email);
        var mobile = await Leer(response);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.InRange(mobile.RefreshExpiresAtUtc, antes.AddDays(30), DateTimeOffset.UtcNow.AddDays(30));
        Assert.InRange(mobile.ExpiresAtUtc, DateTime.UtcNow.AddMinutes(14), DateTime.UtcNow.AddMinutes(16));
        var token = await db.TokensRenovacionMoviles.SingleAsync();
        Assert.Equal(new TokensRenovacion().CalcularHash(mobile.RefreshToken), token.Hash);
        Assert.NotEqual(mobile.RefreshToken, token.Hash);
        Assert.Equal(64, token.Hash.Length);
        var sesion = await db.SesionesMoviles.SingleAsync();
        Assert.Equal((user.Id, acceso.Id, acceso.VersionSesion), (sesion.UsuarioId, sesion.AccesoId, sesion.VersionSesion));
        Assert.Equal(hashAnterior, await db.Usuarios.Where(x => x.Id == user.Id).Select(x => x.PasswordHash).SingleAsync());
        Assert.Equal(HttpStatusCode.OK, await EstadoActual(client, jwtWeb));
        Assert.Equal(HttpStatusCode.OK, await EstadoActual(client, mobile.AccessToken));
    }

    [Fact]
    public async Task CambioDeCredencialRevocaRefreshYUnNuevoIngresoPuedeRenovar()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var (user, _) = await Operador(db);
        var anterior = await Login(client, user.Email);
        user.RevocarSesionesPorCambioDePassword();
        await db.SaveChangesAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, await EstadoActual(client, anterior.AccessToken));
        using var invalidada = await client.PostAsJsonAsync(Ruta + "refresh", new { anterior.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, invalidada.StatusCode);

        var nuevoIngreso = await Login(client, user.Email);
        Assert.Equal(HttpStatusCode.OK, await EstadoActual(client, nuevoIngreso.AccessToken));
        using var renovacion = await client.PostAsJsonAsync(Ruta + "refresh", new { nuevoIngreso.RefreshToken });
        var renovada = await Leer(renovacion);
        Assert.Equal(HttpStatusCode.OK, await EstadoActual(client, renovada.AccessToken));
        Assert.Equal(nuevoIngreso.RefreshExpiresAtUtc, renovada.RefreshExpiresAtUtc);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RotacionFuncionaConJwtPrevioYReutilizacionRevocaSoloSuFamilia(bool jwtExpirado)
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var (user, _) = await Operador(db);
        var primera = await Login(client, user.Email);
        var independiente = await Login(client, user.Email);
        var expired = new JwtSecurityTokenHandler().ReadJwtToken(primera.AccessToken);
        var tokenExpirado = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            AuthenticationTestData.Issuer, AuthenticationTestData.Audience, expired.Claims,
            DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddMinutes(-5),
            new SigningCredentials(new SymmetricSecurityKey(Convert.FromBase64String(AuthenticationTestData.SigningKey)), SecurityAlgorithms.HmacSha256)));
        Assert.Equal(HttpStatusCode.Unauthorized, await EstadoActual(client, tokenExpirado));
        using var request = new HttpRequestMessage(HttpMethod.Post, Ruta + "refresh")
        { Content = JsonContent.Create(new { primera.RefreshToken }) };
        request.Headers.Authorization = new("Bearer", jwtExpirado ? tokenExpirado : primera.AccessToken);
        using var response = await client.SendAsync(request);
        var nueva = await Leer(response);
        Assert.NotEqual(primera.RefreshToken, nueva.RefreshToken);
        Assert.Equal(primera.SesionId, nueva.SesionId);
        Assert.Equal(primera.RefreshExpiresAtUtc, nueva.RefreshExpiresAtUtc);
        Assert.Equal(HttpStatusCode.OK, await EstadoActual(client, nueva.AccessToken));
        using var replayRequest = new HttpRequestMessage(HttpMethod.Post, Ruta + "refresh")
        { Content = JsonContent.Create(new { primera.RefreshToken }) };
        replayRequest.Headers.Authorization = new("Bearer", nueva.AccessToken);
        using var replay = await client.SendAsync(replayRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await EstadoActual(client, nueva.AccessToken));
        using var renovacionRevocada = await client.PostAsJsonAsync(Ruta + "refresh", new { nueva.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, renovacionRevocada.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await EstadoActual(client, independiente.AccessToken));
    }

    [Fact]
    public async Task DosRenovacionesConcurrentesNoEmitenDosSucesoresYRevocanLaFamiliaReutilizada()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var (user, _) = await Operador(db);
        var sesion = await Login(client, user.Email);
        var respuestas = await Task.WhenAll(Enumerable.Range(0, 2)
            .Select(_ => client.PostAsJsonAsync(Ruta + "refresh", new { sesion.RefreshToken })));
        try
        {
            Assert.Single(respuestas, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Single(respuestas, r => r.StatusCode == HttpStatusCode.Unauthorized);
            var ganadora = await Leer(respuestas.Single(x => x.IsSuccessStatusCode));
            Assert.Equal(HttpStatusCode.Unauthorized, await EstadoActual(client, ganadora.AccessToken));
            Assert.NotNull((await db.SesionesMoviles.SingleAsync()).RevocadaEnUtc);
            Assert.Equal(2, await db.TokensRenovacionMoviles.CountAsync());
            Assert.Equal(1, await db.TokensRenovacionMoviles.CountAsync(x => x.ConsumidoEnUtc != null));
        }
        finally { foreach (var response in respuestas) response.Dispose(); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task LogoutEsIdempotenteConSesionVencidaOVigenteYNoCierraOtrasSesiones(bool vencida)
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var (user, _) = await Operador(db);
        var primera = await Login(client, user.Email);
        var otra = await Login(client, user.Email);
        if (vencida)
        {
            var ahora = DateTimeOffset.UtcNow;
            await db.SesionesMoviles.Where(x => x.Id == primera.SesionId).ExecuteUpdateAsync(x =>
                x.SetProperty(s => s.CreadaEnUtc, ahora.AddDays(-31)).SetProperty(s => s.ExpiraEnUtc, ahora.AddSeconds(-1)));
        }
        for (var i = 0; i < 2; i++)
        {
            using var response = await client.PostAsJsonAsync(Ruta + "logout", new { primera.RefreshToken });
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        using var desconocido = await client.PostAsJsonAsync(Ruta + "logout", new { RefreshToken = new TokensRenovacion().Crear().Token });
        Assert.Equal(HttpStatusCode.NoContent, desconocido.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await EstadoActual(client, primera.AccessToken));
        Assert.Equal(HttpStatusCode.OK, await EstadoActual(client, otra.AccessToken));
    }

    [Theory]
    [InlineData("baja")] [InlineData("rol")] [InlineData("version")] [InlineData("suspension")]
    public async Task CambiosDeAccesoVigenteImpidenRenovar(string cambio)
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var (user, acceso) = await Operador(db);
        var sesion = await Login(client, user.Email);
        if (cambio == "baja") acceso.CambiarEstado(false);
        if (cambio == "rol") acceso.RetirarRol();
        if (cambio == "version") acceso.RevocarSesiones();
        if (cambio == "suspension")
            await db.Operadores.Where(x => x.Id == acceso.OperadorId).ExecuteUpdateAsync(x => x.SetProperty(o => o.Estado, EstadoOrganizacion.Suspendida));
        await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, await EstadoActual(client, sesion.AccessToken));
        using var response = await client.PostAsJsonAsync(Ruta + "refresh", new { sesion.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotNull((await db.SesionesMoviles.SingleAsync()).RevocadaEnUtc);
        Assert.Equal(1, await db.TokensRenovacionMoviles.CountAsync());
    }

    [Fact]
    public async Task VencimientoAbsolutoBloqueaJwtTodaviaNoExpiradoYRefresh()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var (user, _) = await Operador(db);
        var sesion = await Login(client, user.Email);
        var ahora = DateTimeOffset.UtcNow;
        await db.SesionesMoviles.ExecuteUpdateAsync(x => x.SetProperty(s => s.CreadaEnUtc, ahora.AddDays(-31))
            .SetProperty(s => s.ExpiraEnUtc, ahora.AddSeconds(-1)));
        Assert.Equal(HttpStatusCode.Unauthorized, await EstadoActual(client, sesion.AccessToken));
        using var response = await client.PostAsJsonAsync(Ruta + "refresh", new { sesion.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(1, await db.TokensRenovacionMoviles.CountAsync());
    }

    [Theory]
    [InlineData("password")] [InlineData("comercio")] [InlineData("suspendida")] [InlineData("identidad")]
    public async Task LoginMovilNoEmiteSesionParaCredencialesOAmbitosIncompatibles(string caso)
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var (user, acceso) = await Operador(db);
        if (caso == "comercio") user = await db.Usuarios.SingleAsync(x => db.AccesosUsuario.Any(a => a.UsuarioId == x.Id && a.ComercioId != null));
        if (caso == "suspendida")
            await db.Operadores.Where(x => x.Id == acceso.OperadorId).ExecuteUpdateAsync(x => x.SetProperty(o => o.Estado, EstadoOrganizacion.Suspendida));
        if (caso == "identidad")
        {
            const string email = "sin-org@example.test";
            user = new Usuario(email, AuthenticationTestData.Hash(email));
            db.Usuarios.Add(user); await db.SaveChangesAsync();
        }
        using var response = await LoginResponse(client, user.Email, caso == "password" ? "incorrecta" : AuthenticationTestData.Password);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await db.SesionesMoviles.CountAsync());
    }

    [Fact]
    public async Task TokensDesconocidosOMalformadosNoCreanEstado()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        foreach (var token in new[] { "", "incorrecto", new string('!', 43) })
        {
            using var response = await client.PostAsJsonAsync(Ruta + "refresh", new { RefreshToken = token });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var desconocido = await client.PostAsJsonAsync(Ruta + "refresh", new { RefreshToken = new TokensRenovacion().Crear().Token });
        Assert.Equal(HttpStatusCode.Unauthorized, desconocido.StatusCode);
        Assert.Equal(0, await db.SesionesMoviles.CountAsync());
    }

    [Fact]
    public async Task LaBaseRechazaSesionQueMezclaUsuarioYAccesoDeOtraOrganizacion()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var (user, _) = await Operador(db);
        var ajeno = await db.AccesosUsuario.SingleAsync(x => x.ComercioId != null);
        var ahora = DateTimeOffset.UtcNow;
        db.SesionesMoviles.Add(new SesionMovil(user.Id, ajeno.Id, ajeno.VersionSesion, user.VersionCredencial, ahora, ahora.AddDays(30)));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var postgresError = Assert.IsType<Npgsql.PostgresException>(error.InnerException);
        Assert.Equal("23503", postgresError.SqlState);
        Assert.Equal("FK_SesionesMoviles_AccesosUsuario_AccesoId_UsuarioId", postgresError.ConstraintName);
    }

    [Fact]
    public async Task OpenApiPublicaLosTresContratosSinModificarElLoginWeb()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = new IntegrationApiFactory(database, environment: "Development");
        using var client = factory.CreateHttpClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var root = document.RootElement;
        var paths = root.GetProperty("paths");
        foreach (var endpoint in new[] { "login", "refresh", "logout" })
            Assert.True(paths.TryGetProperty(Ruta + endpoint, out _));
        var baja = paths.GetProperty("/api/mobile/instalaciones/{id}").GetProperty("delete").GetProperty("responses");
        foreach (var status in new[] { "204", "403", "404" }) Assert.True(baja.TryGetProperty(status, out _));
        var schema = paths.GetProperty(Ruta + "refresh").GetProperty("post").GetProperty("responses")
            .GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema");
        if (schema.TryGetProperty("$ref", out var referencia))
            schema = root.GetProperty("components").GetProperty("schemas").GetProperty(referencia.GetString()!.Split('/')[^1]);
        Assert.Equal(new[] { "accessToken", "expiresAtUtc", "refreshExpiresAtUtc", "refreshToken", "sesionId", "tokenType" },
            schema.GetProperty("properties").EnumerateObject().Select(x => x.Name).Order());
    }

    [Fact]
    public async Task MigracionYRollbackConservanUsuariosHashesRolesYAccesos()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var (user, acceso) = await Operador(db);
        var ids = await db.Usuarios.AsNoTracking().Select(x => x.Id).OrderBy(x => x).ToArrayAsync();
        var rol = acceso.RolId;
        await Login(client, user.Email);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261017035519_AddUsuarioEmailConcurrency");
        await migrator.MigrateAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(ids, await db.Usuarios.AsNoTracking().Select(x => x.Id).OrderBy(x => x).ToArrayAsync());
        Assert.Equal(user.PasswordHash, (await db.Usuarios.SingleAsync(x => x.Id == user.Id)).PasswordHash);
        Assert.Equal(rol, (await db.AccesosUsuario.SingleAsync(x => x.Id == acceso.Id)).RolId);
        Assert.Equal(0, await db.SesionesMoviles.CountAsync());
        Assert.False((await db.Database.GetPendingMigrationsAsync()).Any());
    }

    private static async Task<(Usuario User, AccesoUsuario Acceso)> Operador(NexoRuta.Infrastructure.Persistence.NexoRutaDbContext db)
    {
        var acceso = await db.AccesosUsuario.SingleAsync(x => x.OperadorId != null);
        return (await db.Usuarios.SingleAsync(x => x.Id == acceso.UsuarioId), acceso);
    }
    private static Task<HttpResponseMessage> LoginResponse(HttpClient client, string email, string? password = null)
        => client.PostAsJsonAsync(Ruta + "login", new { Email = email, Password = password ?? AuthenticationTestData.Password });
    private static async Task<SesionMovilResponse> Login(HttpClient client, string email)
    {
        using var response = await LoginResponse(client, email);
        return await Leer(response);
    }
    private static async Task<SesionMovilResponse> Leer(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SesionMovilResponse>())!;
    }
    private static async Task<HttpStatusCode> EstadoActual(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/usuarios/actual");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }
}
