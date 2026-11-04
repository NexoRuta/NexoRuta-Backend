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
        Assert.Equal(31, contracts.Count);
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

    [Fact]
    public async Task ImportIdentityTableAndDefinerFunctionsRemainScopedToCommerceContext()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var connection = await OpenRestrictedConnectionAsync(database, "nexoruta_api");

        await using (var table = new NpgsqlCommand("""
            SELECT c.relrowsecurity, c.relforcerowsecurity,
                   has_table_privilege(current_user, c.oid, 'SELECT'),
                   has_table_privilege(current_user, c.oid, 'INSERT'),
                   has_table_privilege(current_user, c.oid, 'UPDATE'),
                   has_table_privilege(current_user, c.oid, 'DELETE'),
                   r.rolsuper, r.rolbypassrls,
                   EXISTS (SELECT 1 FROM pg_policies p WHERE p.schemaname = 'public'
                       AND p.tablename = 'IdentidadesEnvioImportado'
                       AND p.qual LIKE '%actor_kind%' AND p.qual LIKE '%org_id%'
                       AND p.with_check LIKE '%actor_kind%' AND p.with_check LIKE '%org_id%')
            FROM pg_class c
            CROSS JOIN pg_roles r
            WHERE c.oid = 'public."IdentidadesEnvioImportado"'::regclass
              AND r.rolname = 'nexoruta_shipment_fn'
            """, connection))
        await using (var reader = await table.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0), "RLS must be enabled on private import identities.");
            Assert.True(reader.GetBoolean(1), "RLS must be forced even for the table owner.");
            for (var column = 2; column <= 5; column++)
                Assert.False(reader.GetBoolean(column), "The API role must not have direct table DML privileges.");
            Assert.False(reader.GetBoolean(6), "The scoped function owner must not be a superuser.");
            Assert.False(reader.GetBoolean(7), "The scoped function owner must not bypass RLS.");
            Assert.True(reader.GetBoolean(8), "The identity table policy must scope reads and writes to the authenticated commerce.");
        }

        foreach (var signature in new[]
                 {
                     "nexoruta_app.lock_import_references(text[])",
                     "nexoruta_app.find_import_references(text[])",
                     "nexoruta_app.bind_import_identity(text,uuid,uuid,smallint,bytea)"
                 })
        {
            await using var function = new NpgsqlCommand("""
                SELECT p.prosecdef, owner.rolname, p.proconfig,
                       has_function_privilege(current_user, p.oid, 'EXECUTE'),
                       EXISTS (SELECT 1 FROM aclexplode(COALESCE(p.proacl, acldefault('f', p.proowner))) acl
                           WHERE acl.grantee = 0 AND acl.privilege_type = 'EXECUTE')
                FROM pg_proc p
                JOIN pg_roles owner ON owner.oid = p.proowner
                WHERE p.oid = to_regprocedure(@signature)
                """, connection);
            function.Parameters.AddWithValue("signature", signature);
            await using var reader = await function.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), $"Missing function {signature}.");
            Assert.True(reader.GetBoolean(0), $"{signature} must be SECURITY DEFINER.");
            Assert.Equal("nexoruta_shipment_fn", reader.GetString(1));
            Assert.Contains("search_path=pg_catalog, nexoruta_app", reader.GetFieldValue<string[]>(2));
            Assert.True(reader.GetBoolean(3), $"The API role must execute {signature}.");
            Assert.False(reader.GetBoolean(4), $"PUBLIC must not execute {signature}.");
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
