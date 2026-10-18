using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NexoRuta.Application.Seguimiento.Services;
using NexoRuta.Application.Seguimiento.UseCases;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Seguimiento;
using NexoRuta.Infrastructure.Seguimiento.Consultas;
using NexoRuta.Infrastructure.Seguimiento.Persistence;
using NexoRuta.Infrastructure.Seguimiento.Processing;
using NexoRuta.Infrastructure.Seguimiento.Repositories;
using NexoRuta.Infrastructure.Seguimiento.Security;
using NexoRuta.IntegrationTests.Fixtures;

namespace NexoRuta.IntegrationTests.Seguimiento;

public sealed class RecuperarEnlaceSeguimientoTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData("vigente", HttpStatusCode.OK)]
    [InlineData("pendiente", HttpStatusCode.Accepted)]
    [InlineData("expirado", HttpStatusCode.Gone)]
    [InlineData("revocado", HttpStatusCode.Gone)]
    public async Task RecoversOriginalTokenWithoutWritingAndHonorsValidity(string state, HttpStatusCode expected)
    {
        var database = await postgres.CreateDatabaseAsync();
        var settings = Settings("http://localhost/prefix/");
        using var factory = new IntegrationApiFactory(database, "Development", settings);
        using var client = factory.CreateHttpClient();
        var (envio, owner, _) = await CreateShipmentAsync(database);
        string? original = null;
        var messageId = state == "vigente"
            ? await PublishedTrackingJobTestData.CreatePublishedTrackingJobAsync(database, envio.OperadorId, envio.Id)
            : Guid.Empty;
        await using (var db = database.CreateDbContext())
        {
            if (state == "vigente")
            {
                var processor = Processor(db, settings);
                await processor.EjecutarAsync(messageId, envio.OperadorId, envio.Id);
                original = Cipher(settings).Descifrar(await db.EntregasSeguimiento.SingleAsync());
            }
            else if (state != "pendiente")
            {
                original = GeneradorTokenSeguimiento.Generar();
                var hash = GeneradorTokenSeguimiento.CalcularHash(original);
                var now = DateTimeOffset.UtcNow;
                var link = new EnlaceSeguimiento(envio.OperadorId, envio.Id, hash,
                    now.AddDays(-2), state == "expirado" ? now.AddDays(-1) : now.AddDays(1));
                if (state == "revocado") link.Revocar(now);
                var encrypted = Cipher(settings).Cifrar(original, envio.OperadorId, envio.Id, hash);
                db.AddRange(link, new EntregaSeguimiento(envio.OperadorId, envio.Id, hash,
                    encrypted.TokenCifrado, encrypted.Nonce, encrypted.Tag));
                await db.SaveChangesAsync();
            }
        }
        for (var read = 0; read < 2; read++)
        {
            using var request = await RequestAsync(client, database, envio.Id, owner);
            using var response = await client.SendAsync(request);
            Assert.Equal(expected, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(state, body.GetProperty("estado").GetString());
            if (state == "vigente")
            {
                Assert.Equal($"http://localhost/prefix/api/public/seguimiento/{original}", body.GetProperty("url").GetString());
                Assert.True(body.GetProperty("expiraEnUtc").GetDateTimeOffset() > DateTimeOffset.UtcNow);
                // Resolve the public route in this isolated TestServer without requiring a bearer token.
                using var publicResponse = await client.GetAsync(new Uri(body.GetProperty("url").GetString()!).AbsolutePath["/prefix".Length..]);
                Assert.Equal(HttpStatusCode.OK, publicResponse.StatusCode);
                Assert.Equal("Admitido", (await publicResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("estado").GetString());
            }
            else
                Assert.Equal(JsonValueKind.Null, body.GetProperty("url").ValueKind);
        }
        await using var verification = database.CreateDbContext();
        Assert.Equal(state == "pendiente" ? 0 : 1, await verification.EnlacesSeguimiento.CountAsync());
        Assert.Equal(state == "pendiente" ? 0 : 1, await verification.EntregasSeguimiento.CountAsync());
        if (state == "vigente")
        {
            // Replay the same Rabbit processing operation and a different MessageId for the same shipment.
            await Processor(verification, settings).EjecutarAsync(messageId, envio.OperadorId, envio.Id);
            var secondMessageId = await PublishedTrackingJobTestData.CreatePublishedTrackingJobAsync(database, envio.OperadorId, envio.Id);
            await Processor(verification, settings).EjecutarAsync(secondMessageId, envio.OperadorId, envio.Id);
            Assert.Equal(1, await verification.EnlacesSeguimiento.CountAsync());
            Assert.Equal(1, await verification.EntregasSeguimiento.CountAsync());
            Assert.Equal(original, Cipher(settings).Descifrar(await verification.EntregasSeguimiento.AsNoTracking().SingleAsync()));
        }
    }

    [Fact]
    public async Task IdentityScopeAndEnvironmentAreEnforcedWithoutLeakingShipmentExistence()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = new IntegrationApiFactory(database, "Development", Settings());
        using var client = factory.CreateHttpClient();
        using var anonymousClient = factory.CreateHttpClient();
        var (envio, owner, operatorAccess) = await CreateShipmentAsync(database);
        Guid otherOperator, otherCommerce;
        await using (var db = database.CreateDbContext())
        {
            var op = new Operador("Other operator");
            var commerce = new Comercio("Other commerce");
            var operatorEmail = $"other-op-{Guid.NewGuid():N}@example.test";
            var operatorUser = new Usuario(operatorEmail, AuthenticationTestData.Hash(operatorEmail));
            var commerceEmail = $"other-commerce-{Guid.NewGuid():N}@example.test";
            var commerceUser = new Usuario(commerceEmail, AuthenticationTestData.Hash(commerceEmail));
            var opRole = new Rol("other_operator", "Other operator", op.Id, null);
            var commerceRole = new Rol("other_commerce", "Other commerce", null, commerce.Id);
            var opAccess = new AccesoUsuario(operatorUser.Id, op.Id, null, opRole);
            var commerceAccess = new AccesoUsuario(commerceUser.Id, null, commerce.Id, commerceRole);
            db.AddRange(op, commerce, operatorUser, commerceUser, opRole, commerceRole, opAccess, commerceAccess);
            await db.SaveChangesAsync();
            otherOperator = opAccess.Id; otherCommerce = commerceAccess.Id;
        }
        foreach (var access in new[] { otherOperator, otherCommerce })
        {
            using var request = await RequestAsync(client, database, envio.Id, access);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        using var missing = await RequestAsync(client, database, Guid.NewGuid(), owner);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(missing)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymousClient.GetAsync($"/api/envios/{envio.Id}/seguimiento")).StatusCode);
        using var invalid = new HttpRequestMessage(HttpMethod.Get, $"/api/envios/{envio.Id}/seguimiento");
        invalid.Headers.Add("X-NexoRuta-Acceso", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymousClient.SendAsync(invalid)).StatusCode);
        using var allowed = await RequestAsync(client, database, envio.Id, operatorAccess);
        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(allowed)).StatusCode);
        using var production = new IntegrationApiFactory(database, "Production", Settings());
        using var productionClient = production.CreateHttpClient();
        using var forbidden = await RequestAsync(productionClient, database, envio.Id, owner);
        Assert.Equal(HttpStatusCode.Forbidden, (await productionClient.SendAsync(forbidden)).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("relative")]
    [InlineData("ftp://example.test")]
    [InlineData("https://example.test/?token=unsafe")]
    [InlineData("https://example.test/#fragment")]
    [InlineData("https://user:password@example.test")]
    public async Task InvalidPublicBaseReturnsSafeUnavailableResponse(string? baseUrl)
    {
        var database = await postgres.CreateDatabaseAsync();
        var settings = Settings(baseUrl);
        using var factory = new IntegrationApiFactory(database, "Development", settings);
        using var client = factory.CreateHttpClient();
        var (envio, owner, _) = await CreateShipmentAsync(database);
        var messageId = await PublishedTrackingJobTestData.CreatePublishedTrackingJobAsync(database, envio.OperadorId, envio.Id);
        await using (var db = database.CreateDbContext())
            await Processor(db, settings).EjecutarAsync(messageId, envio.OperadorId, envio.Id);
        using var request = await RequestAsync(client, database, envio.Id, owner);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("TokenCifrado", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingOrDifferentEncryptionKeyNeverReturnsOrRegeneratesToken(bool missing)
    {
        var database = await postgres.CreateDatabaseAsync();
        var originalSettings = Settings();
        var apiSettings = Settings();
        if (missing) apiSettings["Seguimiento:EncryptionKey"] = null;
        using var factory = new IntegrationApiFactory(database, "Development", apiSettings);
        using var client = factory.CreateHttpClient();
        var (envio, owner, _) = await CreateShipmentAsync(database);
        string original;
        await using (var db = database.CreateDbContext())
        {
            var messageId = await PublishedTrackingJobTestData.CreatePublishedTrackingJobAsync(database, envio.OperadorId, envio.Id);
            await Processor(db, originalSettings).EjecutarAsync(messageId, envio.OperadorId, envio.Id);
            original = Cipher(originalSettings).Descifrar(await db.EntregasSeguimiento.SingleAsync());
        }
        using var request = await RequestAsync(client, database, envio.Id, owner);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain(original, await response.Content.ReadAsStringAsync());
        await using var verification = database.CreateDbContext();
        Assert.Equal(1, await verification.EnlacesSeguimiento.CountAsync());
        Assert.Equal(original, Cipher(originalSettings).Descifrar(await verification.EntregasSeguimiento.SingleAsync()));
    }

    private static Dictionary<string, string?> Settings(string? baseUrl = "http://localhost") => new()
    {
        ["Seguimiento:EncryptionKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        ["Tracking:PublicBaseUrl"] = baseUrl
    };

    private static CifradorTokenSeguimiento Cipher(IDictionary<string, string?> settings) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());

    private static ProcesarGeneracionSeguimiento Processor(NexoRuta.Infrastructure.Persistence.NexoRutaDbContext db,
        IDictionary<string, string?> settings) => new(db,
        new CrearEnlaceSeguimientoUseCase(new EfConsultaEstadoEnvio(db), new EfEnlacesSeguimientoRepository(db)), Cipher(settings));

    private static async Task<HttpRequestMessage> RequestAsync(HttpClient client, IntegrationDatabase database, Guid envio, Guid access)
    {
        await client.SetBearerForAccessAsync(database, access);
        return new HttpRequestMessage(HttpMethod.Get, $"/api/envios/{envio}/seguimiento");
    }

    private static async Task<(Envio, Guid Owner, Guid Operator)> CreateShipmentAsync(IntegrationDatabase database)
    {
        await using var db = database.CreateDbContext();
        var owner = await db.AccesosUsuario.SingleAsync(x => x.ComercioId != null);
        var operatorAccess = await db.AccesosUsuario.SingleAsync(x => x.OperadorId != null);
        var recipient = new Destinatario(operatorAccess.OperadorId!.Value, "Recipient");
        var address = new Direccion(operatorAccess.OperadorId.Value, "Address");
        var shipment = new Envio(operatorAccess.OperadorId.Value, owner.ComercioId!.Value,
            owner.UsuarioId, recipient.Id, address.Id);
        db.AddRange(recipient, address, shipment);
        await db.SaveChangesAsync();
        return (shipment, owner.Id, operatorAccess.Id);
    }
}
