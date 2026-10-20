using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.ReglasOperativas.Repositories;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.ReglasOperativas;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.Infrastructure.ReglasOperativas;
using NexoRuta.IntegrationTests.Fixtures;
using Npgsql;

namespace NexoRuta.IntegrationTests.ReglasOperativas;

public sealed class ReglasOperativasPersistenceTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string PreviousMigration = "20261019152350_AddTarifarioOperador";

    [Fact]
    public async Task MigrationCreatesRulesSchemaFromEmptyAndImmediatelyPreviousSchema()
    {
        var empty = await postgres.CreateDatabaseAsync();
        await AssertRulesSchemaAsync(empty);

        var previous = await postgres.CreateDatabaseAsync(PreviousMigration);
        await using (var before = previous.CreateDbContext())
            Assert.DoesNotContain("20261019235212_AddReglasOperativas", await before.Database.GetAppliedMigrationsAsync());
        await using (var after = previous.CreateDbContext())
        {
            await after.Database.MigrateAsync();
            Assert.Contains("20261019235212_AddReglasOperativas", await after.Database.GetAppliedMigrationsAsync());
        }
    }

    [Fact]
    public async Task RestrictedOperatorCanEditPublishAndCopyWhilePublishedRowsStayImmutable()
    {
        var database = await postgres.CreateDatabaseAsync();
        var seed = await SeedAsync(database);
        var api = await CreateRestrictedConnectionAsync(database);
        var options = new DbContextOptionsBuilder<NexoRutaDbContext>().UseNexoRutaPostgres(api).Options;

        await using (var noContext = new NexoRutaDbContext(options))
        await using (var transaction = await noContext.Database.BeginTransactionAsync())
        {
            Assert.Empty(await noContext.ReglasOperativasOperadores.ToListAsync());
            var deniedInsert = await Assert.ThrowsAsync<PostgresException>(() => noContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO public."ReglasOperativasOperador" ("OperadorId", "VersionVigenteId", "UltimoNumeroPublicacion", "ActualizadoUtc")
                VALUES ({seed.OperatorB}, NULL, 0, now())
                """));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, deniedInsert.SqlState);
        }

        var initial = await WithOperatorAsync(database, api, seed.OperatorA, seed.UserA,
            repository => repository.CrearBorradorAsync(seed.OperatorA, seed.UserA));
        await WithOperatorAsync(database, api, seed.OperatorB, seed.UserB,
            repository => repository.CrearBorradorAsync(seed.OperatorB, seed.UserB));
        Assert.Null(initial.MaxIntentosEntrega);
        Assert.Empty(initial.RequisitosPrueba);
        var edited = await WithOperatorAsync(database, api, seed.OperatorA, seed.UserA,
            repository => repository.EditarBorradorAsync(seed.OperatorA, seed.UserA, 0, CompleteConfiguration()));
        Assert.Equal(1, edited.VersionConcurrencia);
        Assert.Equal(3, edited.MaxIntentosEntrega);
        Assert.Equal(2, edited.RequisitosPrueba.Count);
        Assert.Equal(2, edited.Motivos.Count);

        var publication = await WithOperatorAsync(database, api, seed.OperatorA, seed.UserA,
            repository => repository.PublicarBorradorAsync(seed.OperatorA, seed.UserA, 1));
        Assert.Equal(1, publication.NumeroVersionPublicada);
        var afterPublish = await WithOperatorAsync(database, api, seed.OperatorA, seed.UserA,
            repository => repository.ConsultarAsync(seed.OperatorA));
        Assert.Equal(publication.Id, afterPublish.Operador!.VersionVigenteId);
        Assert.Equal(1, afterPublish.Operador.UltimoNumeroPublicacion);
        Assert.Contains(afterPublish.PublicacionVigente!.Motivos,
            item => item.Codigo == "DOMICILIO_CERRADO");

        var copied = await WithOperatorAsync(database, api, seed.OperatorA, seed.UserA,
            repository => repository.CrearBorradorAsync(seed.OperatorA, seed.UserA));
        Assert.Equal(0, copied.VersionConcurrencia);
        Assert.Equal(publication.MaxIntentosEntrega, copied.MaxIntentosEntrega);
        Assert.Equal(publication.Motivos.Select(item => item.Codigo).Order().ToArray(),
            copied.Motivos.Select(item => item.Codigo).Order().ToArray());

        var incomplete = new ReglasOperativasConfiguracion(null, null, null, null, null, [],
            [new("DOMICILIO_CERRADO", "Domicilio cerrado", true),
             new("DIRECCION_INCORRECTA", "Dirección incorrecta", true)]);
        var incompleteCopy = await WithOperatorAsync(database, api, seed.OperatorA, seed.UserA,
            repository => repository.EditarBorradorAsync(seed.OperatorA, seed.UserA, 0, incomplete));
        Assert.Null(incompleteCopy.MaxIntentosEntrega);
        Assert.Empty(incompleteCopy.RequisitosPrueba);
        Assert.Equal(2, incompleteCopy.Motivos.Count);

        var changed = CompleteConfiguration(description: "No había receptor en el domicilio");
        await WithOperatorAsync(database, api, seed.OperatorA, seed.UserA,
            repository => repository.EditarBorradorAsync(seed.OperatorA, seed.UserA, 1, changed));
        var preserved = await WithOperatorAsync(database, api, seed.OperatorA, seed.UserA,
            repository => repository.ObtenerPublicacionAsync(seed.OperatorA, publication.Id));
        Assert.Equal("Domicilio cerrado", Assert.Single(preserved!.Motivos,
            item => item.Codigo == "DOMICILIO_CERRADO").Descripcion);
        await AssertDraftMotiveIdentityProtectedAsync(options, seed, copied.Id);

        await AssertPublishedMutationRejectedAsync(options, seed, publication.Id,
            "UPDATE public.\"VersionesReglasOperativas\" SET \"MaxIntentosEntrega\" = 4 WHERE \"OperadorId\" = {0} AND \"Id\" = {1}",
            PostgresErrorCodes.ObjectNotInPrerequisiteState);
        await AssertPublishedMutationRejectedAsync(options, seed, publication.Id,
            "UPDATE public.\"RequisitosPruebaEntrega\" SET \"Tipo\" = 'Fotografia' WHERE \"OperadorId\" = {0} AND \"VersionId\" = {1}",
            PostgresErrorCodes.InsufficientPrivilege);
        await AssertPublishedMutationRejectedAsync(options, seed, publication.Id,
            "UPDATE public.\"MotivosNoEntregaVersion\" SET \"Descripcion\" = 'Alterado' WHERE \"OperadorId\" = {0} AND \"VersionId\" = {1}",
            PostgresErrorCodes.ObjectNotInPrerequisiteState);

        await AssertActorIsolatedAsync(options, seed.OperatorA, seed.UserA, RlsActorKind.Comercio);
        await AssertActorIsolatedAsync(options, seed.OperatorA, seed.UserA, RlsActorKind.Plataforma);
        await AssertActorIsolatedAsync(options, seed.OperatorA, seed.UserA, RlsActorKind.Operador);
        await AssertActorIsolatedAsync(options, seed.OperatorB, seed.UserB, RlsActorKind.Operador);
        await AssertRulesRlsAsync(database, seed.OperatorA, publication.Id);
    }

    [Fact]
    public async Task RootCreationAndEditPublicationRacesDoNotPartiallyWrite()
    {
        var database = await postgres.CreateDatabaseAsync();
        var seed = await SeedAsync(database);
        var api = await CreateRestrictedConnectionAsync(database);

        var creation = await Task.WhenAll(
            TryCreateAsync(database, api, seed.OperatorA, seed.UserA),
            TryCreateAsync(database, api, seed.OperatorA, seed.UserA));
        Assert.Single(creation.OfType<VersionReglasOperativas>());
        Assert.Single(creation.OfType<ConflictoReglasOperativasException>());

        await WithOperatorAsync(database, api, seed.OperatorA, seed.UserA,
            repository => repository.EditarBorradorAsync(seed.OperatorA, seed.UserA, 0, CompleteConfiguration()));
        var raced = await Task.WhenAll(
            TryEditAsync(database, api, seed.OperatorA, seed.UserA, 1,
                CompleteConfiguration(maxAttempts: 4)),
            TryPublishAsync(database, api, seed.OperatorA, seed.UserA, 1));
        Assert.Single(raced.OfType<VersionReglasOperativas>());
        Assert.Single(raced.OfType<ConflictoReglasOperativasException>());

        var final = await WithOperatorAsync(database, api, seed.OperatorA, seed.UserA,
            repository => repository.ConsultarAsync(seed.OperatorA));
        if (final.Borrador is { } draft)
        {
            Assert.Equal(2, draft.VersionConcurrencia);
            Assert.Equal(4, draft.MaxIntentosEntrega);
            Assert.Null(final.PublicacionVigente);
        }
        else
        {
            Assert.Equal(3, final.PublicacionVigente!.MaxIntentosEntrega);
            Assert.Equal(1, final.PublicacionVigente.NumeroVersionPublicada);
        }
    }

    [Fact]
    public async Task FailureUpdatingPublicationPointerRollsBackPublishedVersion()
    {
        var database = await postgres.CreateDatabaseAsync();
        var seed = await SeedAsync(database);
        var api = await CreateRestrictedConnectionAsync(database);
        var draft = await WithOperatorAsync(database, api, seed.OperatorA, seed.UserA,
            repository => repository.CrearBorradorAsync(seed.OperatorA, seed.UserA));
        await WithOperatorAsync(database, api, seed.OperatorA, seed.UserA,
            repository => repository.EditarBorradorAsync(seed.OperatorA, seed.UserA,
                draft.VersionConcurrencia, CompleteConfiguration()));

        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var constraint = new NpgsqlCommand("""
                ALTER TABLE public."ReglasOperativasOperador"
                ADD CONSTRAINT "ck_test_no_rules_publish" CHECK ("UltimoNumeroPublicacion" = 0)
                """, connection);
            await constraint.ExecuteNonQueryAsync();
        }

        var failure = await Assert.ThrowsAsync<DbUpdateException>(() =>
            WithOperatorAsync(database, api, seed.OperatorA, seed.UserA,
                repository => repository.PublicarBorradorAsync(seed.OperatorA, seed.UserA, 1)));
        Assert.IsType<PostgresException>(failure.InnerException);

        var afterFailure = await WithOperatorAsync(database, api, seed.OperatorA, seed.UserA,
            repository => repository.ConsultarAsync(seed.OperatorA));
        Assert.Null(afterFailure.PublicacionVigente);
        Assert.Null(afterFailure.Operador!.VersionVigenteId);
        Assert.Equal(0, afterFailure.Operador.UltimoNumeroPublicacion);
        Assert.Equal(EstadoVersionReglasOperativas.Borrador, afterFailure.Borrador!.Estado);
        Assert.Equal(1, afterFailure.Borrador.VersionConcurrencia);
        Assert.Null(afterFailure.Borrador.NumeroVersionPublicada);
    }

    private static ReglasOperativasConfiguracion CompleteConfiguration(int maxAttempts = 3,
        string description = "Domicilio cerrado")
        => new(maxAttempts, 1440, PoliticaDevolucionAlAgotarIntentos.DecisionOperativa,
            4320, 1440,
            [TipoPruebaEntrega.FirmaEnPantalla, TipoPruebaEntrega.NombreReceptor],
            [new("DOMICILIO_CERRADO", description, true), new("DIRECCION_INCORRECTA", "Dirección incorrecta", true)]);

    private static async Task<Seed> SeedAsync(IntegrationDatabase database)
    {
        var operatorA = new Operador("Operator A");
        var operatorB = new Operador("Operator B");
        var userA = new Usuario($"a-{Guid.NewGuid():N}@example.test");
        var userB = new Usuario($"b-{Guid.NewGuid():N}@example.test");
        await using var db = database.CreateDbContext();
        db.Operadores.AddRange(operatorA, operatorB);
        db.Usuarios.AddRange(userA, userB);
        await db.SaveChangesAsync();
        return new(operatorA.Id, operatorB.Id, userA.Id, userB.Id);
    }

    private static async Task AssertRulesSchemaAsync(IntegrationDatabase database)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_class WHERE relname = ANY(@names)", connection);
        command.Parameters.AddWithValue("names", new[]
            { "ReglasOperativasOperador", "VersionesReglasOperativas", "RequisitosPruebaEntrega", "MotivosNoEntregaVersion" });
        Assert.Equal(4L, (long)(await command.ExecuteScalarAsync())!);
    }

    private static async Task AssertPublishedMutationRejectedAsync(DbContextOptions<NexoRutaDbContext> options,
        Seed seed, Guid versionId, string sql, string expectedSqlState)
    {
        await using var db = new NexoRutaDbContext(options);
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.ApplyAsync(Actor(seed.OperatorA, seed.UserA));
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync(sql, seed.OperatorA, versionId));
        Assert.Equal(expectedSqlState, exception.SqlState);
    }

    private static async Task AssertDraftMotiveIdentityProtectedAsync(DbContextOptions<NexoRutaDbContext> options,
        Seed seed, Guid versionId)
    {
        await using (var db = new NexoRutaDbContext(options))
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.ApplyAsync(Actor(seed.OperatorA, seed.UserA));
            var identity = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("""
                UPDATE public."MotivosNoEntregaVersion" SET "Codigo" = 'OTRO_MOTIVO'
                 WHERE "OperadorId" = {0} AND "VersionId" = {1} AND "Codigo" = 'DOMICILIO_CERRADO'
                """, seed.OperatorA, versionId));
            Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState, identity.SqlState);
        }
        await using (var db = new NexoRutaDbContext(options))
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.ApplyAsync(Actor(seed.OperatorA, seed.UserA));
            var nullIdentity = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("""
                UPDATE public."MotivosNoEntregaVersion" SET "Codigo" = NULL
                 WHERE "OperadorId" = {0} AND "VersionId" = {1} AND "Codigo" = 'DOMICILIO_CERRADO'
                """, seed.OperatorA, versionId));
            Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState, nullIdentity.SqlState);
        }
        await using (var db = new NexoRutaDbContext(options))
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.ApplyAsync(Actor(seed.OperatorA, seed.UserA));
            var delete = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("""
                DELETE FROM public."MotivosNoEntregaVersion"
                 WHERE "OperadorId" = {0} AND "VersionId" = {1} AND "Codigo" = 'DOMICILIO_CERRADO'
                """, seed.OperatorA, versionId));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, delete.SqlState);
        }
    }

    private static async Task AssertActorIsolatedAsync(DbContextOptions<NexoRutaDbContext> options,
        Guid operatorId, Guid userId, RlsActorKind actorKind)
    {
        await using var db = new NexoRutaDbContext(options);
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (actorKind == RlsActorKind.Operador)
            await db.ApplyAsync(Actor(operatorId, userId));
        else
            await db.ApplyAsync(new RlsActorContext(actorKind, userId, Guid.NewGuid(), operatorId));
        var roots = await db.ReglasOperativasOperadores.ToListAsync();
        if (actorKind == RlsActorKind.Operador)
            Assert.Equal(operatorId, Assert.Single(roots).OperadorId);
        else
        {
            Assert.Empty(roots);
            Assert.Empty(await db.VersionesReglasOperativas.ToListAsync());
        }
    }

    private static async Task AssertRulesRlsAsync(IntegrationDatabase database, Guid operatorId, Guid versionId)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var functions = new NpgsqlCommand("""
            SELECT p.proname, pg_get_userbyid(p.proowner), p.prosecdef,
                   array_to_string(p.proconfig, ','),
                   EXISTS (
                       SELECT 1
                         FROM aclexplode(coalesce(p.proacl, acldefault('f', p.proowner))) a
                        WHERE a.grantee = 0 AND a.privilege_type = 'EXECUTE'),
                   has_function_privilege('nexoruta_api', p.oid, 'EXECUTE'),
                   has_function_privilege('nexoruta_worker', p.oid, 'EXECUTE')
              FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
             WHERE n.nspname = 'nexoruta_app'
               AND p.proname = ANY(@functions)
             ORDER BY p.proname
            """, connection))
        {
            functions.Parameters.AddWithValue("functions", new[]
            {
                "guard_operational_rules_version", "guard_operational_rules_children",
                "guard_operational_rules_root", "assert_current_operational_rules_publication"
            });
            var count = 0;
            await using var reader = await functions.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                count++;
                Assert.Equal("nexoruta_schema_owner", reader.GetString(1));
                Assert.True(reader.GetBoolean(2));
                Assert.Contains("search_path=", reader.GetString(3));
                Assert.False(reader.GetBoolean(4));
                Assert.False(reader.GetBoolean(5));
                Assert.False(reader.GetBoolean(6));
            }
            Assert.Equal(4, count);
        }
        await using var command = new NpgsqlCommand("""
            SELECT c.relname, c.relrowsecurity, c.relforcerowsecurity, pg_get_userbyid(c.relowner),
                   has_table_privilege('nexoruta_worker', c.oid, 'SELECT'),
                   has_table_privilege('nexoruta_api', c.oid, 'SELECT'),
                   has_table_privilege('nexoruta_api', c.oid, 'UPDATE'),
                   has_table_privilege('nexoruta_api', c.oid, 'DELETE')
              FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = 'public' AND c.relname = ANY(@tables) ORDER BY c.relname
            """, connection);
        command.Parameters.AddWithValue("tables", new[]
            { "ReglasOperativasOperador", "VersionesReglasOperativas", "RequisitosPruebaEntrega", "MotivosNoEntregaVersion" });
        var rows = new Dictionary<string, (bool Rls, bool Force, string Owner, bool WorkerRead,
            bool ApiRead, bool ApiUpdate, bool ApiDelete)>();
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                rows.Add(reader.GetString(0), (reader.GetBoolean(1), reader.GetBoolean(2), reader.GetString(3),
                    reader.GetBoolean(4), reader.GetBoolean(5), reader.GetBoolean(6), reader.GetBoolean(7)));
        Assert.Equal(4, rows.Count);
        foreach (var row in rows.Values)
        {
            Assert.True(row.Rls);
            Assert.True(row.Force);
            Assert.Equal("nexoruta_schema_owner", row.Owner);
            Assert.False(row.WorkerRead);
            Assert.True(row.ApiRead);
        }
        Assert.False(rows["ReglasOperativasOperador"].ApiDelete);
        Assert.False(rows["VersionesReglasOperativas"].ApiDelete);
        Assert.True(rows["RequisitosPruebaEntrega"].ApiDelete);
        Assert.False(rows["MotivosNoEntregaVersion"].ApiDelete);
        Assert.False(rows["RequisitosPruebaEntrega"].ApiUpdate);
        Assert.True(rows["MotivosNoEntregaVersion"].ApiUpdate);

        var restricted = await CreateRestrictedConnectionAsync(database);
        var options = new DbContextOptionsBuilder<NexoRutaDbContext>().UseNexoRutaPostgres(restricted).Options;
        await using var db = new NexoRutaDbContext(options);
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.ApplyAsync(Actor(operatorId, Guid.NewGuid()));
        var crossOperator = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public."RequisitosPruebaEntrega" ("OperadorId", "VersionId", "Tipo")
            VALUES ({Guid.NewGuid()}, {versionId}, 'FirmaEnPantalla')
            """));
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState, crossOperator.SqlState);
    }

    private static RlsActorContext Actor(Guid operatorId, Guid userId)
        => new(RlsActorKind.Operador, userId, Guid.NewGuid(), operatorId,
            OrganizationType: TipoAccesoUsuario.Operador);

    private static async Task<T> WithOperatorAsync<T>(IntegrationDatabase database, string connectionString,
        Guid operatorId, Guid userId, Func<EfReglasOperativasRepository, Task<T>> operation)
    {
        var options = new DbContextOptionsBuilder<NexoRutaDbContext>().UseNexoRutaPostgres(connectionString).Options;
        await using var db = new NexoRutaDbContext(options);
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.ApplyAsync(Actor(operatorId, userId));
        var result = await operation(new EfReglasOperativasRepository(db));
        await transaction.CommitAsync();
        return result;
    }

    private static async Task<object> TryCreateAsync(IntegrationDatabase database, string connectionString,
        Guid operatorId, Guid userId)
    {
        try { return await WithOperatorAsync(database, connectionString, operatorId, userId,
            repository => repository.CrearBorradorAsync(operatorId, userId)); }
        catch (ConflictoReglasOperativasException exception) { return exception; }
    }

    private static async Task<object> TryEditAsync(IntegrationDatabase database, string connectionString,
        Guid operatorId, Guid userId, long expected, ReglasOperativasConfiguracion configuration)
    {
        try { return await WithOperatorAsync(database, connectionString, operatorId, userId,
            repository => repository.EditarBorradorAsync(operatorId, userId, expected, configuration)); }
        catch (ConflictoReglasOperativasException exception) { return exception; }
    }

    private static async Task<object> TryPublishAsync(IntegrationDatabase database, string connectionString,
        Guid operatorId, Guid userId, long expected)
    {
        try { return await WithOperatorAsync(database, connectionString, operatorId, userId,
            repository => repository.PublicarBorradorAsync(operatorId, userId, expected)); }
        catch (ConflictoReglasOperativasException exception) { return exception; }
    }

    private static async Task<string> CreateRestrictedConnectionAsync(IntegrationDatabase database)
    {
        var name = $"rules_test_{Guid.NewGuid():N}";
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var owner = new NpgsqlConnection(database.ConnectionString);
        await owner.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE ROLE \"{name}\" LOGIN PASSWORD '{password}' NOSUPERUSER NOBYPASSRLS; GRANT nexoruta_api TO \"{name}\"", owner);
        await create.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Username = name,
            Password = password,
            Pooling = false
        }.ConnectionString;
    }

    private sealed record Seed(Guid OperatorA, Guid OperatorB, Guid UserA, Guid UserB);
}
