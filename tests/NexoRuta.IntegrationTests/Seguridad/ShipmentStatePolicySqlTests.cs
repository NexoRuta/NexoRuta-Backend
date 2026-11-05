using NexoRuta.Domain.Envios;
using NexoRuta.IntegrationTests.Fixtures;
using Npgsql;
namespace NexoRuta.IntegrationTests.Seguridad;

public sealed class ShipmentStatePolicySqlTests(PostgresFixture postgres):IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task ActualPostgresGuardAgreesWithDomainForEveryPairIncludingNonTransitionUpdates()
    {
        var data=await postgres.CreateDatabaseAsync();
        await using var connection=new NpgsqlConnection(data.ConnectionString);
        await connection.OpenAsync();
        // Exercise the installed guard itself, without bypassing shipment history on real Envios.
        await using(var create=new NpgsqlCommand("""
            CREATE TEMP TABLE cu11_state_probe ("Estado" text NOT NULL);
            CREATE TRIGGER cu11_probe BEFORE UPDATE OF "Estado" ON cu11_state_probe
                FOR EACH ROW EXECUTE FUNCTION public.guard_shipment_state_transition();
            """,connection))await create.ExecuteNonQueryAsync();
        var pairs=0;
        foreach(var origin in Enum.GetValues<EstadoEnvio>())
        foreach(var destination in Enum.GetValues<EstadoEnvio>())
        {
            await using var transaction=await connection.BeginTransactionAsync();
            await using(var insert=new NpgsqlCommand("INSERT INTO cu11_state_probe VALUES (@state)",connection,transaction))
            {insert.Parameters.AddWithValue("state",origin.ToString());await insert.ExecuteNonQueryAsync();}
            await using var update=new NpgsqlCommand("UPDATE cu11_state_probe SET \"Estado\"=@state",connection,transaction);
            update.Parameters.AddWithValue("state",destination.ToString());
            // Rewriting the same value is not a commercial transition; PostgreSQL permits it.
            if(origin==destination || TransicionesEstadoEnvio.Permite(origin,destination))
                Assert.Equal(1,await update.ExecuteNonQueryAsync());
            else
            {
                var failure=await Assert.ThrowsAsync<PostgresException>(()=>update.ExecuteNonQueryAsync());
                Assert.Equal("23514",failure.SqlState);Assert.Equal("CK_Envios_TransicionEstado",failure.ConstraintName);
            }
            await transaction.RollbackAsync();pairs++;
        }
        Assert.Equal(Enum.GetValues<EstadoEnvio>().Length * Enum.GetValues<EstadoEnvio>().Length,pairs);
    }
    [Fact]
    public async Task ActualParcelGuardAgreesWithDeclarativeResultsForEveryPair()
    {
        var data = await postgres.CreateDatabaseAsync();
        await using var connection = new NpgsqlConnection(data.ConnectionString);
        await connection.OpenAsync();
        await using (var create = new NpgsqlCommand("""
            CREATE TEMP TABLE parcel_result_probe ("Resultado" text NOT NULL);
            CREATE TRIGGER parcel_probe BEFORE UPDATE OF "Resultado" ON parcel_result_probe
                FOR EACH ROW EXECUTE FUNCTION public.guard_parcel_result_transition();
            """, connection)) await create.ExecuteNonQueryAsync();
        foreach (var origin in Enum.GetValues<ResultadoBulto>())
        foreach (var destination in Enum.GetValues<ResultadoBulto>())
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var insert = new NpgsqlCommand("INSERT INTO parcel_result_probe VALUES (@state)", connection, transaction))
            { insert.Parameters.AddWithValue("state", origin.ToString()); await insert.ExecuteNonQueryAsync(); }
            await using var update = new NpgsqlCommand("UPDATE parcel_result_probe SET \"Resultado\"=@state", connection, transaction);
            update.Parameters.AddWithValue("state", destination.ToString());
            if (origin == destination || TransicionesResultadoBulto.Permite(origin, destination))
                Assert.Equal(1, await update.ExecuteNonQueryAsync());
            else
            {
                var failure = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
                Assert.Equal("23514", failure.SqlState);
            }
            await transaction.RollbackAsync();
        }
    }
}
