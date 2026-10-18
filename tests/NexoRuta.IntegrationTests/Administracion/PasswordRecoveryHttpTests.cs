using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexoRuta.Application.Administracion;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Administracion;
using NexoRuta.Infrastructure.Administracion.Autenticacion;
using NexoRuta.Infrastructure.Outbox;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.IntegrationTests.Fixtures;

namespace NexoRuta.IntegrationTests.Administracion;

public sealed class PasswordRecoveryHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task OwnerReceivesGenericEmailAndResetSurvivesApiRestartAndRevokesOldJwt()
    {
        var database = await postgres.CreateDatabaseAsync();
        var keyDirectory = Directory.CreateTempSubdirectory("nexoruta-password-recovery-");
        try
        {
            var settings = new Dictionary<string, string?> { ["DataProtection:KeysDirectory"] = keyDirectory.FullName };
            string ownerEmail;
            string oldJwt;
            (Guid UserId, string Token) link;
            string existingResponse;

            using (var firstFactory = new IntegrationApiFactory(database, settings: settings))
            using (var firstClient = firstFactory.CreateHttpClient())
            {
                Assert.Equal(HttpStatusCode.OK, (await firstClient.GetAsync("/health/live")).StatusCode);
                await using var db = database.CreateDbContext();
                var ownerAccess = await db.AccesosUsuario.SingleAsync(x => x.ComercioId != null && x.EsPropietario);
                var owner = await db.Usuarios.SingleAsync(x => x.Id == ownerAccess.UsuarioId);
                ownerEmail = owner.Email!;
                Assert.True(ownerAccess.EsPropietario);

                oldJwt = await LoginAsync(firstClient, ownerEmail, AuthenticationTestData.Password);
                using var existing = await RequestRecoveryAsync(firstClient, ownerEmail);
                using var missing = await RequestRecoveryAsync(firstClient, "missing@example.test");
                Assert.Equal(HttpStatusCode.OK, existing.StatusCode);
                Assert.Equal(HttpStatusCode.OK, missing.StatusCode);
                existingResponse = await existing.Content.ReadAsStringAsync();
                Assert.Equal(existingResponse, await missing.Content.ReadAsStringAsync());
                Assert.DoesNotContain("token", existingResponse, StringComparison.OrdinalIgnoreCase);
                link = await ReadLinkFromOutboxAsync(firstFactory.Services, db, ownerEmail);
            }

            // A second API host with the same persisted Data Protection keys must still accept the live Identity token.
            using (var secondFactory = new IntegrationApiFactory(database, settings: settings))
            using (var client = secondFactory.CreateHttpClient())
            {
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
                using var weakPassword = await ResetAsync(client, link.UserId, link.Token, "short");
                Assert.Equal(HttpStatusCode.BadRequest, weakPassword.StatusCode);
                Assert.Contains("12 caracteres", await weakPassword.Content.ReadAsStringAsync(), StringComparison.Ordinal);

                const string newPassword = "Owner-new-password-2026!";
                using var reset = await ResetAsync(client, link.UserId, link.Token, newPassword);
                Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
                using var reused = await ResetAsync(client, link.UserId, link.Token, "Another-new-password-2026!");
                Assert.Equal(HttpStatusCode.BadRequest, reused.StatusCode);

                using var oldSession = await SendBearerAsync(client, oldJwt, "/api/usuarios/actual");
                Assert.Equal(HttpStatusCode.Unauthorized, oldSession.StatusCode);
                using var oldLogin = await client.PostAsJsonAsync("/api/auth/login", new { Email = ownerEmail, Password = AuthenticationTestData.Password });
                Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
                Assert.NotEmpty(await LoginAsync(client, ownerEmail, newPassword));

                await using var db = database.CreateDbContext();
                var accessAfter = await db.AccesosUsuario.SingleAsync(x => x.UsuarioId == link.UserId);
                Assert.True(accessAfter.EsPropietario);
                Assert.True(accessAfter.Activo);
                Assert.Equal(0, accessAfter.VersionSesion);
                Assert.Equal(1, (await db.Usuarios.SingleAsync(x => x.Id == link.UserId)).VersionCredencial);
            }
        }
        finally { keyDirectory.Delete(true); }
    }

    [Fact]
    public async Task ExistingAndUnknownAccountsHaveTheSamePublicResponseAndOnlyExistingCreatesOutbox()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = new IntegrationApiFactory(database);
        using var client = factory.CreateHttpClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        await using var db = database.CreateDbContext();
        var owner = await db.Usuarios.FirstAsync();

        using var known = await RequestRecoveryAsync(client, owner.Email!);
        using var unknown = await RequestRecoveryAsync(client, "missing@example.test");
        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        var publicMessage = await known.Content.ReadAsStringAsync();
        Assert.Equal(publicMessage, await unknown.Content.ReadAsStringAsync());
        Assert.Equal(2, await db.SecurityOutboxMessages.CountAsync());
        Assert.DoesNotContain(owner.Email!, publicMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecoveryRequestsAreRateLimited()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = new IntegrationApiFactory(database);
        using var client = factory.CreateHttpClient();
        var responses = new List<HttpResponseMessage>();
        try
        {
            for (var attempt = 0; attempt < 6; attempt++)
                responses.Add(await RequestRecoveryAsync(client, "missing@example.test"));
            Assert.All(responses.Take(5), response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            Assert.Equal(HttpStatusCode.TooManyRequests, responses[5].StatusCode);
            var body = await responses[0].Content.ReadAsStringAsync();
            foreach (var response in responses.Take(5))
                Assert.Equal(body, await response.Content.ReadAsStringAsync());
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task InvalidExpiredAndCrossAccountTokensCannotChangePasswords()
    {
        var database = await postgres.CreateDatabaseAsync();
        var keyDirectory = Directory.CreateTempSubdirectory("nexoruta-password-recovery-expired-");
        try
        {
            var settings = new Dictionary<string, string?>
            {
                ["DataProtection:KeysDirectory"] = keyDirectory.FullName,
                ["PasswordRecovery:TokenLifespanSeconds"] = "1"
            };
            using var factory = new IntegrationApiFactory(database, settings: settings);
            using var client = factory.CreateHttpClient();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
            await using var db = database.CreateDbContext();
            var ownerAccess = await db.AccesosUsuario.SingleAsync(x => x.ComercioId != null && x.EsPropietario);
            var owner = await db.Usuarios.SingleAsync(x => x.Id == ownerAccess.UsuarioId);
            var token = await GenerateResetTokenAsync(factory, owner.Email!);

            using var foreignUser = await ResetAsync(client, Guid.NewGuid(), token, "Unauthorized-password-2026!");
            Assert.Equal(HttpStatusCode.BadRequest, foreignUser.StatusCode);
            using var invalid = await ResetAsync(client, owner.Id, "invalid-token", "Unauthorized-password-2026!");
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            await Task.Delay(TimeSpan.FromSeconds(2));
            using var expired = await ResetAsync(client, owner.Id, token, "Unauthorized-password-2026!");
            Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
            Assert.NotEmpty(await LoginAsync(client, owner.Email!, AuthenticationTestData.Password));
            Assert.Equal(0, (await db.Usuarios.SingleAsync(x => x.Id == owner.Id)).VersionCredencial);
        }
        finally { keyDirectory.Delete(true); }
    }

    [Fact]
    public async Task ResetRevokesCommerceOperatorPlatformAndUnassignedIdentityJwtSessions()
    {
        var database = await postgres.CreateDatabaseAsync();
        var keyDirectory = Directory.CreateTempSubdirectory("nexoruta-password-recovery-scopes-");
        try
        {
            using var factory = new IntegrationApiFactory(database,
                settings: new Dictionary<string, string?> { ["DataProtection:KeysDirectory"] = keyDirectory.FullName });
            using var client = factory.CreateHttpClient();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);

            const string platformEmail = "platform-reset@example.test";
            await using (var db = database.CreateDbContext())
            {
                var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["PlatformInitialization:Enabled"] = "true",
                    ["PlatformInitialization:Email"] = platformEmail,
                    ["PlatformInitialization:Password"] = AuthenticationTestData.Password
                }).Build();
                await new DatosInicialesSeeder(db, new AccesoInicial("unused@example.test", "Unused", "Unused", "Unused"),
                    settings, AuthenticationTestData.UserManager(db), AuthenticationTestData.PlatformRepository(db)).SeedPlataformaAsync();
            }

            const string identityEmail = "identity-only-reset@example.test";
            using (var scope = factory.Services.CreateScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<Usuario>>();
                var result = await users.CreateAsync(new Usuario(identityEmail), AuthenticationTestData.Password);
                Assert.True(result.Succeeded);
            }

            await using var readDb = database.CreateDbContext();
            var commerceUser = await (from access in readDb.AccesosUsuario
                                      join user in readDb.Usuarios on access.UsuarioId equals user.Id
                                      where access.ComercioId != null && access.EsPropietario
                                      select user).SingleAsync();
            var operatorUser = await (from access in readDb.AccesosUsuario
                                      join user in readDb.Usuarios on access.UsuarioId equals user.Id
                                      where access.OperadorId != null
                                      select user).FirstAsync();
            var platformUser = await (from access in readDb.AccesosPlataforma
                                      join user in readDb.Usuarios on access.UsuarioId equals user.Id
                                      select user).SingleAsync();

            var accounts = new[]
            {
                (Name: "commerce", Email: commerceUser.Email!, Scope: "Comercio"),
                (Name: "operator", Email: operatorUser.Email!, Scope: "Operador"),
                (Name: "platform", Email: platformUser.Email!, Scope: "Plataforma"),
                (Name: "identity", Email: identityEmail, Scope: "SinOrganizacion")
            };
            var sessions = new List<(string Name, Guid UserId, string Scope, string Jwt, string ResetToken, string NewPassword)>();
            using (var scope = factory.Services.CreateScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<Usuario>>();
                foreach (var account in accounts)
                {
                    using var login = await client.PostAsJsonAsync("/api/auth/login", new { Email = account.Email, Password = AuthenticationTestData.Password });
                    Assert.Equal(HttpStatusCode.OK, login.StatusCode);
                    var loginJson = await login.Content.ReadFromJsonAsync<JsonElement>();
                    Assert.Equal(account.Scope, loginJson.GetProperty("contexto").GetString());
                    var jwt = loginJson.GetProperty("accessToken").GetString()!;
                    var user = await users.FindByEmailAsync(account.Email);
                    Assert.NotNull(user);
                    sessions.Add((account.Name, user.Id, account.Scope, jwt,
                        await users.GeneratePasswordResetTokenAsync(user), $"New-{account.Name}-password-2026!"));
                }
            }

            foreach (var session in sessions)
            {
                using var reset = await ResetAsync(client, session.UserId, session.ResetToken, session.NewPassword);
                Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
            }
            foreach (var session in sessions)
            {
                using var oldSession = await SendBearerAsync(client, session.Jwt, "/api/usuarios/actual");
                Assert.Equal(HttpStatusCode.Unauthorized, oldSession.StatusCode);
            }

            using (var scope = factory.Services.CreateScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<Usuario>>();
                foreach (var session in sessions)
                {
                    var user = await users.FindByIdAsync(session.UserId.ToString());
                    Assert.NotNull(user);
                    Assert.True(await users.CheckPasswordAsync(user, session.NewPassword));
                }
            }

            readDb.ChangeTracker.Clear();
            var ownerAfter = await readDb.AccesosUsuario.SingleAsync(x => x.UsuarioId == commerceUser.Id);
            var platformAfter = await readDb.AccesosPlataforma.SingleAsync(x => x.UsuarioId == platformUser.Id);
            Assert.True(ownerAfter.EsPropietario);
            Assert.True(ownerAfter.Activo);
            Assert.Equal(0, ownerAfter.VersionSesion);
            Assert.True(platformAfter.Activo);
            Assert.Equal(0, platformAfter.VersionSesion);
            foreach (var session in sessions)
                Assert.Equal(1, (await readDb.Usuarios.SingleAsync(x => x.Id == session.UserId)).VersionCredencial);
        }
        finally { keyDirectory.Delete(true); }
    }

    [Fact]
    public async Task RecoveryDoesNotReactivateDisabledAccessOrSuspendedOrganization()
    {
        var database = await postgres.CreateDatabaseAsync();
        var keyDirectory = Directory.CreateTempSubdirectory("nexoruta-password-recovery-suspended-");
        try
        {
            using var factory = new IntegrationApiFactory(database,
                settings: new Dictionary<string, string?> { ["DataProtection:KeysDirectory"] = keyDirectory.FullName });
            using var client = factory.CreateHttpClient();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);

            await using var db = database.CreateDbContext();
            var access = await db.AccesosUsuario.SingleAsync(x => x.OperadorId != null);
            var operatorOrganization = await db.Operadores.SingleAsync(x => x.Id == access.OperadorId);
            var user = await db.Usuarios.SingleAsync(x => x.Id == access.UsuarioId);
            access.CambiarEstado(false);
            operatorOrganization.CambiarEstado(EstadoOrganizacion.Suspendida);
            await db.SaveChangesAsync();

            using var requested = await RequestRecoveryAsync(client, user.Email!);
            Assert.Equal(HttpStatusCode.OK, requested.StatusCode);
            var link = await ReadLinkFromOutboxAsync(factory.Services, db, user.Email!);
            using var reset = await ResetAsync(client, link.UserId, link.Token, "Operator-recovered-password-2026!");
            Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

            db.ChangeTracker.Clear();
            Assert.False((await db.AccesosUsuario.SingleAsync(x => x.Id == access.Id)).Activo);
            Assert.Equal(EstadoOrganizacion.Suspendida,
                (await db.Operadores.SingleAsync(x => x.Id == operatorOrganization.Id)).Estado);
            using var login = await client.PostAsJsonAsync("/api/auth/login", new
            {
                Email = user.Email,
                Password = "Operator-recovered-password-2026!"
            });
            Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        }
        finally { keyDirectory.Delete(true); }
    }

    private static async Task<string> LoginAsync(HttpClient client, string email, string password)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { Email = email, Password = password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    private static Task<HttpResponseMessage> RequestRecoveryAsync(HttpClient client, string email)
        => client.PostAsJsonAsync("/api/auth/password-recovery/request", new { Email = email });

    private static Task<HttpResponseMessage> ResetAsync(HttpClient client, Guid userId, string token, string password)
        => client.PostAsJsonAsync("/api/auth/password-recovery/reset", new { UsuarioId = userId, Token = token, NuevaPassword = password });

    private static async Task<string> GenerateResetTokenAsync(IntegrationApiFactory factory, string email)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Usuario>>();
        var user = await users.FindByEmailAsync(email);
        Assert.NotNull(user);
        return await users.GeneratePasswordResetTokenAsync(user);
    }

    private static async Task<(Guid UserId, string Token)> ParseLink(string body)
    {
        var link = new Uri(body.Split('\n', StringSplitOptions.RemoveEmptyEntries).Single(line => line.StartsWith("https://", StringComparison.Ordinal)));
        Assert.Equal("commerce.example.test", link.Host);
        var values = link.Query.TrimStart('?').Split('&').Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]), StringComparer.Ordinal);
        return (Guid.Parse(values["userId"]), values["token"]);
    }

    private static Task<HttpResponseMessage> SendBearerAsync(HttpClient client, string token, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private static async Task<(Guid UserId, string Token)> ReadLinkFromOutboxAsync(
        IServiceProvider services, NexoRutaDbContext db, string recipient)
    {
        var emailMessage = await db.SecurityOutboxMessages.SingleAsync(x => x.Type == SecurityOutboxMessageTypes.PasswordRecoveryEmail);
        var eventMessage = await db.SecurityOutboxMessages.SingleAsync(x => x.Type == SecurityOutboxMessageTypes.PasswordRecoveryRequested);
        Assert.DoesNotContain(recipient, emailMessage.Payload, StringComparison.Ordinal);
        using var eventJson = JsonDocument.Parse(eventMessage.Payload);
        var eventFields = eventJson.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray();
        Assert.Equal(new[] { "eventId", "occurredAt", "schemaVersion", "usuarioId" }, eventFields);
        Assert.Equal(eventMessage.Id.ToString("D"), eventJson.RootElement.GetProperty("eventId").GetString());

        var protector = services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(PasswordRecoveryDataProtection.EmailCommandPurpose);
        var plaintext = protector.Unprotect(emailMessage.Payload);
        var command = JsonSerializer.Deserialize<PasswordRecoveryEmailCommand>(plaintext, PasswordRecoveryJson.Options);
        Assert.NotNull(command);
        Assert.Equal(recipient, command.Recipient);
        return await ParseLink(command.Link);
    }
}
