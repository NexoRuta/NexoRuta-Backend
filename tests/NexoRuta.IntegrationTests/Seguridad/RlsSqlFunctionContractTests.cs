using System.Security.Cryptography;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.IntegrationTests.Fixtures;
using Npgsql;

namespace NexoRuta.IntegrationTests.Seguridad;

public sealed class RlsSqlFunctionContractTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task ConsumedFunctionsMatchExactPostgresSignaturesForRestrictedRuntimeRoles()
    {
        var contracts = RlsSqlFunctionContract.Functions;
        Assert.Equal(26, contracts.Count);
        Assert.Equal(contracts.Count, contracts.Select(contract => contract.Identifier).Distinct().Count());
        Assert.All(contracts, contract => Assert.Contains(contract.RuntimeRole, new[] { "nexoruta_api", "nexoruta_worker" }));

        var database = await postgres.CreateDatabaseAsync();
        foreach (var runtimeRole in new[] { "nexoruta_api", "nexoruta_worker" })
        {
            await using var connection = await OpenRestrictedConnectionAsync(database, runtimeRole);
            foreach (var contract in contracts.Where(contract => contract.RuntimeRole == runtimeRole))
            {
                var mismatch = await FindMismatchAsync(connection,
                    $"{contract.Identifier}({contract.Arguments})", contract.Result);
                Assert.True(mismatch is null, mismatch);
            }

            Assert.NotNull(await FindMismatchAsync(connection,
                "nexoruta_app.missing_contract_function(uuid)", "boolean"));
            Assert.NotNull(await FindMismatchAsync(connection,
                "nexoruta_app.login_lookup(uuid)", "TABLE(\"UsuarioId\" uuid)"));
            Assert.NotNull(await FindMismatchAsync(connection,
                "nexoruta_app.login_lookup(text)", "boolean"));
        }
    }

    private static async Task<string?> FindMismatchAsync(
        NpgsqlConnection connection, string signature, string expectedResult)
    {
        await using var command = new NpgsqlCommand("""
            SELECT pg_get_function_result(p.oid), has_function_privilege(current_user, p.oid, 'EXECUTE')
            FROM pg_proc p
            WHERE p.oid = to_regprocedure(@signature) AND p.prokind = 'f'
            """, connection);
        command.Parameters.AddWithValue("signature", signature);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return $"Missing exact SQL function signature: {signature}.";
        var result = reader.GetString(0);
        if (result != expectedResult)
            return $"Incompatible SQL function result for {signature}: expected {expectedResult}; found {result}.";
        return reader.GetBoolean(1) ? null : $"Runtime role cannot execute {signature}.";
    }

    private static async Task<NpgsqlConnection> OpenRestrictedConnectionAsync(
        IntegrationDatabase database, string runtimeRole)
    {
        Assert.Contains(runtimeRole, new[] { "nexoruta_api", "nexoruta_worker" });
        var role = $"contract_{Guid.NewGuid():N}";
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using (var owner = new NpgsqlConnection(database.ConnectionString))
        {
            await owner.OpenAsync();
            await using var create = new NpgsqlCommand(
                $"CREATE ROLE {role} LOGIN PASSWORD '{password}' NOSUPERUSER NOBYPASSRLS; GRANT {runtimeRole} TO {role};", owner);
            await create.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Username = role, Password = password, Pooling = false
        }.ConnectionString;
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var check = new NpgsqlCommand("""
            SELECT current_user, rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user
            """, connection);
        await using var reader = await check.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(role, reader.GetString(0));
        Assert.False(reader.GetBoolean(1));
        Assert.False(reader.GetBoolean(2));
        return connection;
    }
}
