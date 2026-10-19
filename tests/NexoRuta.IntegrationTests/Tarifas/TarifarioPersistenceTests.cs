using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Tarifas;
using NexoRuta.Domain.ZonasCobertura;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.Infrastructure.Tarifas;
using NexoRuta.IntegrationTests.Fixtures;
using Npgsql;

namespace NexoRuta.IntegrationTests.Tarifas;

public sealed class TarifarioPersistenceTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string PreviousMigration = "20261019010048_AddCoverageZonesAndDeliverySlots";

    [Fact]
    public async Task MigrationFromEmptyAndImmediatelyPreviousSchemaCreatesTariffTables()
    {
        var empty = await postgres.CreateDatabaseAsync();
        await AssertTariffTablesAsync(empty);

        var previous = await postgres.CreateDatabaseAsync(PreviousMigration);
        await using (var before = previous.CreateDbContext())
            Assert.DoesNotContain("20261019152350_AddTarifarioOperador", await before.Database.GetAppliedMigrationsAsync());
        await using (var after = previous.CreateDbContext())
        {
            await after.Database.MigrateAsync();
            Assert.Contains("20261019152350_AddTarifarioOperador", await after.Database.GetAppliedMigrationsAsync());
        }
    }

    [Fact]
    public async Task RestrictedOperatorCanEditPublishAndCopyWithoutChangingInactiveZoneRates()
    {
        var database = await postgres.CreateDatabaseAsync();
        var seed = await SeedAsync(database);
        var api = await CreateRestrictedConnectionAsync(database, "nexoruta_api");
        var options = new DbContextOptionsBuilder<NexoRutaDbContext>().UseNexoRutaPostgres(api.ConnectionString).Options;
        var actor = Actor(seed.OperatorA, seed.UserA);
        VersionTarifario publication;
        VersionTarifario copiedDraft;
        await using (var db = new NexoRutaDbContext(options))
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.ApplyAsync(actor);
            var repository = new EfTarifariosRepository(db);
            var draft = await repository.CrearBorradorAsync(seed.OperatorA, seed.UserA);
            var configuration = Configuration(seed.ZoneA, seed.ZoneInactiveA);
            var edited = await repository.EditarBorradorAsync(seed.OperatorA, seed.UserA, 0, configuration);
            Assert.Equal(1, edited.VersionConcurrencia);
            await Assert.ThrowsAsync<ConflictoTarifarioException>(() => repository.EditarBorradorAsync(
                seed.OperatorA, seed.UserA, 0, configuration));
            var afterConflict = await repository.ConsultarAsync(seed.OperatorA);
            Assert.Equal(1, Assert.Single(afterConflict.Versiones, x => x.Estado == EstadoVersionTarifario.Borrador).VersionConcurrencia);

            publication = await repository.PublicarBorradorAsync(seed.OperatorA, seed.UserA, 1);
            Assert.Equal(EstadoVersionTarifario.Publicada, publication.Estado);
            Assert.Equal(1, publication.NumeroVersionPublicada);
            copiedDraft = await repository.CrearBorradorAsync(seed.OperatorA, seed.UserA);
            Assert.Equal(2, copiedDraft.Tarifas.Count);
            Assert.Contains(copiedDraft.Tarifas, x => x.ZonaId == seed.ZoneInactiveA && x.Modalidad == ModalidadTarifaria.Urgente);
            await transaction.CommitAsync();
        }

        await using (var verify = database.CreateDbContext())
        {
            var persisted = await verify.VersionesTarifario.Include(x => x.Tarifas).ThenInclude(x => x.Ajustes)
                .SingleAsync(x => x.OperadorId == seed.OperatorA && x.Id == publication.Id);
            var rate = persisted.Tarifas.Single(x => x.ZonaId == seed.ZoneA);
            Assert.Equal(12.34m, rate.ImporteBaseUyu);
            Assert.Equal(5.123456m, rate.PrecioPorKilogramoUyu);
            Assert.Equal(1.111111m, rate.PrecioPorMetroCubicoUyu);
            Assert.Equal(7.25m, Assert.Single(rate.Ajustes).ImporteFijoUyu);
            Assert.Equal(EstadoVersionTarifario.Borrador,
                (await verify.VersionesTarifario.SingleAsync(x => x.Id == copiedDraft.Id)).Estado);
        }

        await using (var otherTenant = new NexoRutaDbContext(options))
        {
            await using var transaction = await otherTenant.Database.BeginTransactionAsync();
            await otherTenant.ApplyAsync(Actor(seed.OperatorB, seed.UserB));
            var repository = new EfTarifariosRepository(otherTenant);
            var ownDraft = await repository.CrearBorradorAsync(seed.OperatorB, seed.UserB);
            Assert.Equal(seed.OperatorB, ownDraft.OperadorId);
            var configured = await repository.EditarBorradorAsync(seed.OperatorB, seed.UserB, 0,
                [new(seed.ZoneB, ModalidadTarifaria.Estandar, 10m, 0m, 0m, [])]);
            var inactiveConflict = await Assert.ThrowsAsync<ConflictoTarifarioException>(() =>
                repository.PublicarBorradorAsync(seed.OperatorB, seed.UserB, configured.VersionConcurrencia));
            Assert.Equal(ReglasTarifario.Errors.NoActiveOffer, inactiveConflict.Code);
            var unchanged = await repository.ConsultarAsync(seed.OperatorB);
            Assert.Null(unchanged.Tarifario!.VersionVigenteId);
            Assert.Equal(0, unchanged.Tarifario.UltimoNumeroPublicacion);
            Assert.Single(unchanged.Versiones, x => x.Estado == EstadoVersionTarifario.Borrador);
            await transaction.CommitAsync();
        }

        await AssertRlsAndChildImmutabilityAsync(database, api.ConnectionString, seed, publication);
        await AssertCrossOperatorForeignKeyAsync(database, seed, copiedDraft);
    }

    [Fact]
    public async Task CompetingEditsAndPublicationsSerializeOnTheSameOperatorRoot()
    {
        var database = await postgres.CreateDatabaseAsync();
        var seed = await SeedAsync(database);
        var api = await CreateRestrictedConnectionAsync(database, "nexoruta_api");
        var draft = await WithOperatorAsync(database, api.ConnectionString, seed.OperatorA, seed.UserA,
            repository => repository.CrearBorradorAsync(seed.OperatorA, seed.UserA));

        var editResults = await Task.WhenAll(
            TryEditAsync(database, api.ConnectionString, seed, 0, 10m),
            TryEditAsync(database, api.ConnectionString, seed, 0, 20m));
        Assert.Single(editResults.OfType<VersionTarifario>());
        Assert.Single(editResults.OfType<ConflictoTarifarioException>());

        var editedDraft = await WithOperatorAsync(database, api.ConnectionString, seed.OperatorA, seed.UserA,
            repository => repository.ConsultarAsync(seed.OperatorA));
        var version = Assert.Single(editedDraft.Versiones, x => x.Estado == EstadoVersionTarifario.Borrador);
        Assert.Equal(1, version.VersionConcurrencia);

        var publishResults = await Task.WhenAll(
            TryPublishAsync(database, api.ConnectionString, seed, version.VersionConcurrencia),
            TryPublishAsync(database, api.ConnectionString, seed, version.VersionConcurrencia));
        Assert.Single(publishResults.OfType<VersionTarifario>());
        Assert.Single(publishResults.OfType<ConflictoTarifarioException>());

        var finalState = await WithOperatorAsync(database, api.ConnectionString, seed.OperatorA, seed.UserA,
            repository => repository.ConsultarAsync(seed.OperatorA));
        Assert.Equal(1, finalState.Tarifario!.UltimoNumeroPublicacion);
        Assert.Single(finalState.Versiones, x => x.Estado == EstadoVersionTarifario.Publicada);
        Assert.DoesNotContain(finalState.Versiones, x => x.Estado == EstadoVersionTarifario.Borrador);
    }

    [Fact]
    public async Task EditAndPublishRaceCannotPublishPartialOrStaleConfiguration()
    {
        var database = await postgres.CreateDatabaseAsync();
        var seed = await SeedAsync(database);
        var api = await CreateRestrictedConnectionAsync(database, "nexoruta_api");
        await WithOperatorAsync(database, api.ConnectionString, seed.OperatorA, seed.UserA,
            repository => repository.CrearBorradorAsync(seed.OperatorA, seed.UserA));
        await WithOperatorAsync(database, api.ConnectionString, seed.OperatorA, seed.UserA,
            repository => repository.EditarBorradorAsync(seed.OperatorA, seed.UserA, 0,
                [new(seed.ZoneA, ModalidadTarifaria.Estandar, 10m, 1m, 0m, [])]));

        var results = await Task.WhenAll(
            TryEditAsync(database, api.ConnectionString, seed, 1, 20m),
            TryPublishAsync(database, api.ConnectionString, seed, 1));
        Assert.Single(results.OfType<VersionTarifario>());
        Assert.Single(results.OfType<ConflictoTarifarioException>());

        var after = await WithOperatorAsync(database, api.ConnectionString, seed.OperatorA, seed.UserA,
            repository => repository.ConsultarAsync(seed.OperatorA));
        var draft = after.Versiones.SingleOrDefault(x => x.Estado == EstadoVersionTarifario.Borrador);
        var publication = after.Versiones.SingleOrDefault(x => x.Estado == EstadoVersionTarifario.Publicada);
        if (draft is not null)
        {
            Assert.Null(publication);
            Assert.Equal(2, draft.VersionConcurrencia);
            Assert.Equal(20m, draft.Tarifas.Single().ImporteBaseUyu);
        }
        else
        {
            Assert.NotNull(publication);
            Assert.Equal(1, publication.NumeroVersionPublicada);
            Assert.Equal(10m, publication.Tarifas.Single().ImporteBaseUyu);
        }
    }

    private static async Task AssertTariffTablesAsync(IntegrationDatabase database)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_class WHERE relname = ANY(@names)", connection);
        command.Parameters.AddWithValue("names", new[] { "TarifariosOperador", "VersionesTarifario", "TarifasZonaModalidad", "AjustesTarifario" });
        Assert.Equal(4L, (long)(await command.ExecuteScalarAsync())!);
    }

    private static async Task<Seed> SeedAsync(IntegrationDatabase database)
    {
        var operatorA = new Operador("Operator A");
        var operatorB = new Operador("Operator B");
        var userA = new Usuario($"a-{Guid.NewGuid():N}@example.test");
        var userB = new Usuario($"b-{Guid.NewGuid():N}@example.test");
        var factory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        var geometryA = factory.CreateMultiPolygon([factory.CreatePolygon(
            [new(-56, -34), new(-55, -34), new(-55, -33), new(-56, -33), new(-56, -34)])]);
        var geometryB = factory.CreateMultiPolygon([factory.CreatePolygon(
            [new(-54, -34), new(-53, -34), new(-53, -33), new(-54, -33), new(-54, -34)])]);
        var geometryAInactive = factory.CreateMultiPolygon([factory.CreatePolygon(
            [new(-52, -34), new(-51, -34), new(-51, -33), new(-52, -33), new(-52, -34)])]);
        var zoneA = ZonaCobertura.Crear(operatorA.Id, "Active A", geometryA,
            franjasIniciales: [new(DiaSemanaIso.Lunes, new TimeOnly(9, 0), new TimeOnly(10, 0))]);
        zoneA.Activar(zoneA.Version);
        var zoneB = ZonaCobertura.Crear(operatorB.Id, "Inactive B", geometryB);
        var zoneAInactive = ZonaCobertura.Crear(operatorA.Id, "Inactive A", geometryAInactive);

        await using var db = database.CreateDbContext();
        db.Operadores.AddRange(operatorA, operatorB);
        db.Usuarios.AddRange(userA, userB);
        db.ZonasCobertura.AddRange(zoneA, zoneB, zoneAInactive);
        await db.SaveChangesAsync();
        return new(operatorA.Id, operatorB.Id, userA.Id, userB.Id, zoneA.Id, zoneAInactive.Id, zoneB.Id);
    }

    private static IReadOnlyCollection<TarifaZonaModalidadConfiguracion> Configuration(Guid zoneA, Guid inactiveZoneA)
        =>
        [
            new(zoneA, ModalidadTarifaria.Estandar, 12.34m, 5.123456m, 1.111111m,
                [new("Packing", TipoAjusteTarifario.RecargoFijo, 7.25m)]),
            new(inactiveZoneA, ModalidadTarifaria.Urgente, 0m, 0m, 0m, [])
        ];

    private static RlsActorContext Actor(Guid operatorId, Guid userId)
        => new(RlsActorKind.Operador, userId, Guid.NewGuid(), operatorId,
            OrganizationType: TipoAccesoUsuario.Operador);

    private static async Task AssertRlsAndChildImmutabilityAsync(IntegrationDatabase database,
        string apiConnectionString, Seed seed, VersionTarifario publication)
    {
        var role = new NpgsqlConnectionStringBuilder(apiConnectionString);
        await using (var connection = new NpgsqlConnection(apiConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                SELECT c.relname, c.relrowsecurity, c.relforcerowsecurity, pg_get_userbyid(c.relowner),
                       has_table_privilege('nexoruta_worker', c.oid, 'SELECT'),
                       has_table_privilege('nexoruta_api', c.oid, 'SELECT'),
                       has_table_privilege('nexoruta_api', c.oid, 'INSERT'),
                       has_table_privilege('nexoruta_api', c.oid, 'UPDATE'),
                       has_table_privilege('nexoruta_api', c.oid, 'DELETE')
                FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'public' AND c.relname = ANY(@tables)
                ORDER BY c.relname
                """, connection);
            command.Parameters.AddWithValue("tables", new[]
                { "TarifariosOperador", "VersionesTarifario", "TarifasZonaModalidad", "AjustesTarifario" });
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new Dictionary<string, (bool Rls, bool Force, string Owner, bool WorkerRead,
                bool ApiRead, bool ApiInsert, bool ApiUpdate, bool ApiDelete)>();
            while (await reader.ReadAsync())
                rows.Add(reader.GetString(0), (reader.GetBoolean(1), reader.GetBoolean(2), reader.GetString(3),
                    reader.GetBoolean(4), reader.GetBoolean(5), reader.GetBoolean(6), reader.GetBoolean(7), reader.GetBoolean(8)));
            Assert.Equal(4, rows.Count);
            foreach (var row in rows.Values)
            {
                Assert.True(row.Rls);
                Assert.True(row.Force);
                Assert.Equal("nexoruta_schema_owner", row.Owner);
                Assert.False(row.WorkerRead);
                Assert.True(row.ApiRead);
                Assert.True(row.ApiInsert);
                Assert.True(row.ApiUpdate);
            }
            Assert.False(rows["TarifariosOperador"].ApiDelete);
            Assert.False(rows["VersionesTarifario"].ApiDelete);
            Assert.True(rows["TarifasZonaModalidad"].ApiDelete);
            Assert.True(rows["AjustesTarifario"].ApiDelete);
        }

        var restrictedOptions = new DbContextOptionsBuilder<NexoRutaDbContext>().UseNexoRutaPostgres(role.ConnectionString).Options;
        await using (var noContext = new NexoRutaDbContext(restrictedOptions))
        {
            await using var transaction = await noContext.Database.BeginTransactionAsync();
            Assert.Equal(0, await noContext.TarifariosOperador.CountAsync());
            await noContext.ApplyAsync(new RlsActorContext(RlsActorKind.Comercio, seed.UserA, Guid.NewGuid(),
                seed.OperatorA, OrganizationType: TipoAccesoUsuario.Comercio));
            Assert.Equal(0, await noContext.TarifariosOperador.CountAsync());
            await noContext.ApplyAsync(RlsActorContext.From(new ContextoPlataforma(
                Guid.NewGuid(), Guid.NewGuid(), 0, true, "platform@example.test")));
            Assert.Equal(0, await noContext.TarifariosOperador.CountAsync());
            await noContext.ApplyAsync(Actor(seed.OperatorA, seed.UserA));
            Assert.Equal(0, await noContext.TarifariosOperador.CountAsync(x => x.OperadorId == seed.OperatorB));
            Assert.Equal(1, await noContext.TarifariosOperador.CountAsync(x => x.OperadorId == seed.OperatorA));
        }

        await AssertPublishedUpdateRejectedAsync(restrictedOptions, seed,
            "UPDATE public.\"VersionesTarifario\" SET \"VersionConcurrencia\" = \"VersionConcurrencia\" WHERE \"OperadorId\" = {0} AND \"Id\" = {1}", publication.Id);
        await AssertPublishedUpdateRejectedAsync(restrictedOptions, seed,
            "UPDATE public.\"TarifasZonaModalidad\" SET \"ImporteBaseUyu\" = 999 WHERE \"OperadorId\" = {0} AND \"VersionId\" = {1}", publication.Id);
        await AssertPublishedUpdateRejectedAsync(restrictedOptions, seed,
            "UPDATE public.\"AjustesTarifario\" SET \"Nombre\" = \"Nombre\" WHERE \"OperadorId\" = {0} AND \"VersionId\" = {1}", publication.Id);
    }

    private static async Task AssertPublishedUpdateRejectedAsync(DbContextOptions<NexoRutaDbContext> options,
        Seed seed, string sql, Guid versionId)
    {
        await using var db = new NexoRutaDbContext(options);
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.ApplyAsync(Actor(seed.OperatorA, seed.UserA));
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync(sql, seed.OperatorA, versionId));
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState, exception.SqlState);
    }

    private static async Task AssertCrossOperatorForeignKeyAsync(IntegrationDatabase database, Seed seed,
        VersionTarifario draft)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO public."TarifasZonaModalidad" ("OperadorId", "VersionId", "ZonaId", "Modalidad", "ImporteBaseUyu", "PrecioPorKilogramoUyu", "PrecioPorMetroCubicoUyu")
            VALUES (@operatorId, @versionId, @otherZoneId, 'Estandar', 1, 0, 0)
            """, connection);
        command.Parameters.AddWithValue("operatorId", seed.OperatorA);
        command.Parameters.AddWithValue("versionId", draft.Id);
        command.Parameters.AddWithValue("otherZoneId", seed.ZoneB);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
    }

    private static async Task<object?> TryEditAsync(IntegrationDatabase database, string connectionString,
        Seed seed, long expectedVersion, decimal baseAmount)
    {
        try
        {
            return await WithOperatorAsync(database, connectionString, seed.OperatorA, seed.UserA,
                repository => repository.EditarBorradorAsync(seed.OperatorA, seed.UserA, expectedVersion,
                    [new(seed.ZoneA, ModalidadTarifaria.Estandar, baseAmount, 1m, 0m, [])]));
        }
        catch (ConflictoTarifarioException exception) { return exception; }
    }

    private static async Task<object?> TryPublishAsync(IntegrationDatabase database, string connectionString,
        Seed seed, long expectedVersion)
    {
        try
        {
            return await WithOperatorAsync(database, connectionString, seed.OperatorA, seed.UserA,
                repository => repository.PublicarBorradorAsync(seed.OperatorA, seed.UserA, expectedVersion));
        }
        catch (ConflictoTarifarioException exception) { return exception; }
    }

    private static async Task<T> WithOperatorAsync<T>(IntegrationDatabase database, string connectionString,
        Guid operatorId, Guid userId, Func<EfTarifariosRepository, Task<T>> operation)
    {
        var options = new DbContextOptionsBuilder<NexoRutaDbContext>().UseNexoRutaPostgres(connectionString).Options;
        await using var db = new NexoRutaDbContext(options);
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.ApplyAsync(Actor(operatorId, userId));
        var result = await operation(new EfTarifariosRepository(db));
        await transaction.CommitAsync();
        return result;
    }

    private static async Task<(string ConnectionString, string Role)> CreateRestrictedConnectionAsync(
        IntegrationDatabase database, string memberRole)
    {
        var name = $"tariff_test_{Guid.NewGuid():N}";
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var owner = new NpgsqlConnection(database.ConnectionString);
        await owner.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE ROLE \"{name}\" LOGIN PASSWORD '{password}' NOSUPERUSER NOBYPASSRLS; GRANT {memberRole} TO \"{name}\"", owner);
        await create.ExecuteNonQueryAsync();
        var connectionString = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Username = name,
            Password = password,
            Pooling = false
        }.ConnectionString;
        return (connectionString, name);
    }

    private sealed record Seed(Guid OperatorA, Guid OperatorB, Guid UserA, Guid UserB,
        Guid ZoneA, Guid ZoneInactiveA, Guid ZoneB);
}
