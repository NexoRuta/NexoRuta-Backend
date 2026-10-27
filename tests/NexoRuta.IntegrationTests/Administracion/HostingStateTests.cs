using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexoRuta.Infrastructure.Hosting;
using NexoRuta.IntegrationTests.Fixtures;
using Npgsql;

namespace NexoRuta.IntegrationTests.Administracion;

public sealed class HostingStateTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task SharedRingImportsLegacyKeysAndWorksAcrossIndependentHosts()
    {
        var database = await postgres.CreateDatabaseAsync();
        var directory = Directory.CreateTempSubdirectory("nexoruta-hosting-keys-");
        try
        {
            var connection = await CreateRingAsync(database, "api");
            var certificate = WriteCertificate(directory.FullName);
            var legacy = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(directory.FullName, "legacy")),
                builder => builder.SetApplicationName("NexoRuta.Api"));
            var protectedLegacyMessage = legacy.CreateProtector("pending-email").Protect("pending payload");
            await using var first = Provider(connection, "api", "NexoRuta.Api", certificate);
            Assert.Equal(1, await SharedDataProtection.ImportAsync(first, Path.Combine(directory.FullName, "legacy")));
            Assert.Equal(0, await SharedDataProtection.ImportAsync(first, Path.Combine(directory.FullName, "legacy")));
            Assert.Equal("pending payload", first.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("pending-email").Unprotect(protectedLegacyMessage));

            first.GetRequiredService<IKeyManager>().CreateNewKey(DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddDays(90));
            await using var second = Provider(connection, "api", "NexoRuta.Api", certificate);
            var protectedNewMessage = second.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("pending-email").Protect("cross-host payload");
            await using var third = Provider(connection, "api", "NexoRuta.Api", certificate);
            Assert.Equal("cross-host payload", third.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("pending-email").Unprotect(protectedNewMessage));

            await using var sql = new NpgsqlConnection(connection);
            await sql.OpenAsync();
            await using var check = new NpgsqlCommand(
                "SELECT count(*) FROM nexoruta_keys_api.\"DataProtectionKeys\" WHERE \"Xml\" LIKE '%encryptedSecret%'", sql);
            Assert.True(Convert.ToInt64(await check.ExecuteScalarAsync()) >= 1);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task PortalCredentialsCannotReadAnotherRingOrBusinessTables()
    {
        var database = await postgres.CreateDatabaseAsync();
        var directory = Directory.CreateTempSubdirectory("nexoruta-hosting-isolation-");
        try
        {
            var adminConnection = await CreateRingAsync(database, "admin");
            var commerceConnection = await CreateRingAsync(database, "commerce");
            var certificate = WriteCertificate(directory.FullName);
            await using var admin = Provider(adminConnection, "admin", "NexoRuta.Admin", certificate);
            await using var commerce = Provider(commerceConnection, "commerce", "NexoRuta.Commerce", certificate);
            var protectedAdminCookie = admin.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("cookie").Protect("platform session");
            Assert.Throws<CryptographicException>(() => commerce.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("cookie").Unprotect(protectedAdminCookie));

            await using var sql = new NpgsqlConnection(commerceConnection);
            await sql.OpenAsync();
            foreach (var query in new[]
            {
                "SELECT * FROM nexoruta_keys_admin.\"DataProtectionKeys\"",
                "SELECT * FROM public.\"Usuarios\"",
                "SELECT * FROM public.\"OutboxMessages\""
            })
            {
                await using var forbidden = new NpgsqlCommand(query, sql);
                var denied = await Assert.ThrowsAsync<PostgresException>(() => forbidden.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
            }
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public void SharedRingWithoutCertificateFailsBeforeStarting()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DataProtection"] = "Host=127.0.0.1;Database=unused"
        }).Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection()
            .AddSharedDataProtection(configuration, "NexoRuta.Api", "api"));
    }

    private static ServiceProvider Provider(string connection, string ring, string applicationName, string certificate)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DataProtection"] = connection,
            ["DataProtection:CertificatePath"] = certificate
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        Assert.True(services.AddSharedDataProtection(configuration, applicationName, ring));
        return services.BuildServiceProvider();
    }

    private static string WriteCertificate(string directory)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=NexoRuta hosting integration", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var path = Path.Combine(directory, "keys.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx));
        return path;
    }

    private static async Task<string> CreateRingAsync(IntegrationDatabase database, string ring)
    {
        var role = "keys_test_" + Guid.NewGuid().ToString("N");
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var schema = "nexoruta_keys_" + ring;
        await using var sql = new NpgsqlConnection(database.ConnectionString);
        await sql.OpenAsync();
        // The test setup mirrors the technical-only schema; the actual key IO uses the official provider.
        await using var setup = new NpgsqlCommand($$"""
            CREATE ROLE "{{role}}" LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS PASSWORD '{{password}}';
            CREATE SCHEMA "{{schema}}" AUTHORIZATION "{{role}}";
            REVOKE ALL ON SCHEMA "{{schema}}" FROM PUBLIC;
            CREATE TABLE "{{schema}}"."DataProtectionKeys" (
                "Id" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                "FriendlyName" text NULL, "Xml" text NULL);
            ALTER TABLE "{{schema}}"."DataProtectionKeys" OWNER TO "{{role}}";
            """, sql);
        await setup.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Username = role,
            Password = password,
            Pooling = false
        }.ConnectionString;
    }
}
