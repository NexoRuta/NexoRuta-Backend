using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Api.Contracts.Auth;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Administracion.Repositories;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.IntegrationTests.Fixtures;

namespace NexoRuta.IntegrationTests.Administracion;

public sealed class DatosDemoFixtureCargaHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string PlatformEmail = "opt06-platform@example.test";
    private const string Password = AuthenticationTestData.Password;
    private const string LoadEmail = "opt06-load-001@nexoruta.test";

    [Fact]
    public async Task LoadFixtureSeederCreatesLeastPrivilegeAccountAndStableReplaysIdempotently()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using (var db = database.CreateDbContext())
            await new EfPlataformaRepository(db, AuthenticationTestData.UserManager(db))
                .InicializarAsync(PlatformEmail, Password);

        using var factory = new IntegrationApiFactory(database,
            settings: new Dictionary<string, string?> { ["DatabaseInitialization:SeedData"] = "false" });
        using var client = factory.CreateHttpClient();
        await Login(client, PlatformEmail, Password);

        var request = new
        {
            Cuentas = new[] { new { Email = LoadEmail, Password = "opt06-integration-password-2026" } }
        };
        using var firstResponse = await client.PostAsJsonAsync("/api/admin/demo-data/load-fixture", request);
        Assert.True(firstResponse.StatusCode == HttpStatusCode.OK,
            $"Expected fixture seed to succeed, got {(int)firstResponse.StatusCode}: {await firstResponse.Content.ReadAsStringAsync()}");
        var first = await firstResponse.Content.ReadFromJsonAsync<JsonElement>();
        var firstAccount = first.GetProperty("cuentas")[0];
        var journeyId = firstAccount.GetProperty("jornadaId").GetGuid();
        var firstOperations = firstAccount.GetProperty("operaciones").EnumerateArray().ToArray();
        Assert.Equal(50, firstOperations.Length);
        Assert.Equal(Enumerable.Range(2, 50), firstOperations.Select(x => x.GetProperty("secuencia").GetInt32()));
        Assert.All(firstOperations, operation =>
        {
            Assert.Equal("Posicion", operation.GetProperty("tipo").GetString());
            Assert.Equal(JsonValueKind.Null, operation.GetProperty("envioId").ValueKind);
            Assert.Equal(JsonValueKind.Null, operation.GetProperty("versionEnvio").ValueKind);
        });

        using var loadClient = factory.CreateHttpClient();
        await Login(loadClient, LoadEmail, "opt06-integration-password-2026");
        var permissions = await loadClient.GetFromJsonAsync<string[]>("/api/usuarios/actual/permisos");
        Assert.Equal(new[] { "reparto.ejecutar" }, permissions);

        using var secondResponse = await client.PostAsJsonAsync("/api/admin/demo-data/load-fixture", request);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var second = await secondResponse.Content.ReadFromJsonAsync<JsonElement>();
        var secondAccount = second.GetProperty("cuentas")[0];
        Assert.Equal(journeyId, secondAccount.GetProperty("jornadaId").GetGuid());
        Assert.Equal(firstOperations.Select(x => x.GetProperty("id").GetGuid()),
            secondAccount.GetProperty("operaciones").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()));

        await using var verification = database.CreateDbContext();
        var loadUser = await verification.Usuarios.AsNoTracking().SingleAsync(x => x.Email == LoadEmail);
        var access = await verification.AccesosUsuario.AsNoTracking().SingleAsync(x => x.UsuarioId == loadUser.Id);
        Assert.False(access.EsPropietario);
        Assert.Equal(CatalogoPermisos.Para(TipoAccesoUsuario.Operador)
            .Single(x => x.Codigo == "reparto.ejecutar").Codigo,
            (await loadClient.GetFromJsonAsync<string[]>("/api/usuarios/actual/permisos"))!.Single());
    }

    private static async Task Login(HttpClient client, string email, string password)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { Email = email, Password = password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = (await response.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}
