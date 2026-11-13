using Npgsql;

namespace NexoRuta.IntegrationTests.Fixtures;

internal static class SharedRateLimitTestStore
{
    public static async Task InstallAlwaysAllowingStoreAsync(IntegrationDatabase database)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            CREATE SCHEMA nexoruta_hosting;
            CREATE TABLE nexoruta_hosting.rate_limits ("key" text PRIMARY KEY);
            CREATE FUNCTION nexoruta_hosting.take_rate_limit(text, integer) RETURNS boolean
                LANGUAGE sql AS 'SELECT true';
            """, connection);
        await command.ExecuteNonQueryAsync();
    }
}
