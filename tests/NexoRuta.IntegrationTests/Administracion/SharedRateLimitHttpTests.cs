using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Infrastructure.Administracion;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;
using Npgsql;

namespace NexoRuta.IntegrationTests.Administracion;

public sealed class SharedRateLimitHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task LoginTenantIsolationAndRevocationWorkAcrossTwoRealApis()
    {
        var database = await postgres.CreateDatabaseAsync();
        const string emailA = "nfr13-commerce-a@example.test";
        const string emailB = "nfr13-commerce-b@example.test";
        Guid operatorA, accessA;
        await using (var owner = database.CreateDbContext())
        {
            await AuthenticationTestData.Seeder(owner,
                new AccesoInicial(emailA, "NFR13 operator A", "NFR13 commerce A", "nfr13-operator-a@example.test")).SeedAsync();
            await AuthenticationTestData.Seeder(owner,
                new AccesoInicial(emailB, "NFR13 operator B", "NFR13 commerce B", "nfr13-operator-b@example.test")).SeedAsync();
            await ReglasEnvioTestData.SembrarInicialesAsync(owner);
            await ServiciosEnvioTestData.SembrarAsync(owner);
            var operatorUser = await owner.Usuarios.SingleAsync(user => user.Email == "nfr13-operator-a@example.test");
            operatorA = (await owner.AccesosUsuario.SingleAsync(access => access.UsuarioId == operatorUser.Id)).OperadorId!.Value;
            var userA = await owner.Usuarios.SingleAsync(user => user.Email == emailA);
            accessA = (await owner.AccesosUsuario.SingleAsync(access => access.UsuarioId == userA.Id)).Id;
        }
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(database);
        await InstallControlledCounter(database);
        await using var sharedKeys = await SharedDataProtectionTestStore.CreateAsync(database);
        var settings = RlsRestrictedHttpFlowTests.ApiSettings();
        settings["ConnectionStrings:RateLimits"] = runtime.ConnectionString;
        sharedKeys.Configure(settings);
        using var first = new IntegrationApiFactory(runtime, "Production", settings, seedOperationalRules: false);
        using var second = new IntegrationApiFactory(runtime, "Production", settings, seedOperationalRules: false);
        first.UseKestrel(0);
        second.UseKestrel(0);
        using var clientA = first.CreateClient();
        using var clientB = second.CreateClient();
        var tokenA = await Login(clientA, emailA);
        var tokenB = await Login(clientB, emailB);
        clientA.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);
        using var created = await clientA.PostAsJsonAsync("/api/envios", new
        {
            OperadorId = operatorA, Servicio = ServiciosEnvioTestData.Declarar(operatorA),
            DestinatarioNombre = "NFR13 recipient", DestinatarioEmail = "nfr13-recipient@example.test",
            Direccion = "18 de Julio 123",
            Bultos = new[] { new { Codigo = "NFR13-SESSION", PesoGramos = 500m, LargoCentimetros = 20m, AnchoCentimetros = 10m, AltoCentimetros = 5m } }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var shipment = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        clientB.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);
        using (var response = await clientB.GetAsync("/api/envios"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains((await response.Content.ReadFromJsonAsync<JsonElement[]>())!, item => item.GetProperty("id").GetGuid() == shipment);
        }
        clientB.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenB);
        using (var response = await clientB.GetAsync("/api/envios"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain((await response.Content.ReadFromJsonAsync<JsonElement[]>())!, item => item.GetProperty("id").GetGuid() == shipment);
        }
        await using (var owner = database.CreateDbContext())
        {
            (await owner.AccesosUsuario.SingleAsync(access => access.Id == accessA)).RevocarSesiones();
            await owner.SaveChangesAsync();
        }
        clientB.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);
        using var revoked = await clientB.GetAsync("/api/envios");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        clientB.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenB);
        using var otherStillValid = await clientB.GetAsync("/api/envios");
        Assert.Equal(HttpStatusCode.OK, otherStillValid.StatusCode);
    }

    private static async Task<string> Login(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { Email = email, Password = AuthenticationTestData.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    [Fact]
    public async Task TwoRealApisUseOneSharedQuotaForAlternatingRequests()
    {
        var database = await postgres.CreateDatabaseAsync();
        using (var seed = database.CreateApiFactory()) using (seed.CreateHttpClient()) { }
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(database);
        await InstallControlledCounter(database);
        await using var sharedKeys = await SharedDataProtectionTestStore.CreateAsync(database);
        var settings = RlsRestrictedHttpFlowTests.ApiSettings();
        settings["ConnectionStrings:RateLimits"] = runtime.ConnectionString;
        sharedKeys.Configure(settings);
        using var first = new IntegrationApiFactory(runtime, "Production", settings, seedOperationalRules: false);
        using var second = new IntegrationApiFactory(runtime, "Production", settings, seedOperationalRules: false);
        first.UseKestrel(0);
        second.UseKestrel(0);
        using var clientA = first.CreateClient();
        using var clientB = second.CreateClient();
        Assert.NotEqual(clientA.BaseAddress, clientB.BaseAddress);
        using var readyA = await clientA.GetAsync("/ready");
        using var readyB = await clientB.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.OK, readyA.StatusCode);
        Assert.Equal(HttpStatusCode.OK, readyB.StatusCode);

        var requests = Enumerable.Range(0, 6).Select(i =>
        {
            var client = i % 2 == 0 ? clientA : clientB;
            return client.PostAsJsonAsync("/api/auth/login", new
            {
                Email = "missing@example.test", Password = AuthenticationTestData.Password
            });
        }).ToArray();
        var responses = await Task.WhenAll(requests);
        try
        {
            Assert.Equal(5, responses.Count(response => response.StatusCode == HttpStatusCode.Unauthorized));
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.TooManyRequests);
        }
        finally { foreach (var response in responses) response.Dispose(); }

        await using var sql = new NpgsqlConnection(database.ConnectionString);
        await sql.OpenAsync();
        await using var attempts = new NpgsqlCommand("SELECT last_value FROM nexoruta_hosting.test_attempts", sql);
        Assert.Equal(6L, (long)(await attempts.ExecuteScalarAsync())!);
        await using var restricted = new NpgsqlConnection(runtime.ConnectionString);
        await restricted.OpenAsync();
        await using var forbidden = new NpgsqlCommand("SELECT * FROM nexoruta_hosting.rate_limits", restricted);
        var error = await Assert.ThrowsAsync<PostgresException>(() => forbidden.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    private static async Task InstallControlledCounter(IntegrationDatabase database)
    {
        // A single counter is sufficient for this HTTP test's one caller and one policy.
        // The production function and its real minute windows are covered by cloudsql_compat.py.
        var owner = $"rate_test_{Guid.NewGuid():N}";
        await using var sql = new NpgsqlConnection(database.ConnectionString);
        await sql.OpenAsync();
        await using var command = new NpgsqlCommand($$"""
            CREATE ROLE "{{owner}}" NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
            CREATE SCHEMA nexoruta_hosting AUTHORIZATION "{{owner}}";
            REVOKE ALL ON SCHEMA nexoruta_hosting FROM PUBLIC;
            CREATE TABLE nexoruta_hosting.rate_limits ("key" text PRIMARY KEY);
            ALTER TABLE nexoruta_hosting.rate_limits OWNER TO "{{owner}}";
            CREATE SEQUENCE nexoruta_hosting.test_attempts;
            ALTER SEQUENCE nexoruta_hosting.test_attempts OWNER TO "{{owner}}";
            CREATE FUNCTION nexoruta_hosting.take_rate_limit(text, integer) RETURNS boolean
                LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog
                AS 'SELECT nextval(''nexoruta_hosting.test_attempts''::regclass) <= $2';
            ALTER FUNCTION nexoruta_hosting.take_rate_limit(text, integer) OWNER TO "{{owner}}";
            REVOKE ALL ON FUNCTION nexoruta_hosting.take_rate_limit(text, integer) FROM PUBLIC;
            GRANT USAGE ON SCHEMA nexoruta_hosting TO nexoruta_api;
            GRANT EXECUTE ON FUNCTION nexoruta_hosting.take_rate_limit(text, integer) TO nexoruta_api;
            """, sql);
        await command.ExecuteNonQueryAsync();
    }
}
