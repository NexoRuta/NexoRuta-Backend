using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.ZonasCobertura;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.Infrastructure.ZonasCobertura;
using NexoRuta.IntegrationTests.Fixtures;
using Npgsql;

namespace NexoRuta.IntegrationTests.ZonasCobertura;

public sealed class ZonaCoberturaPostgisTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly Guid ActorA = Guid.Parse("599d1154-8e3e-420a-b944-4987c8d934a2");
    private static readonly Guid ActorB = Guid.Parse("8b15cb79-a329-47ab-a114-f1654ad9fd43");

    [Fact]
    public async Task MigrationFromEmptyDatabasePersistsPostgisZonesSlotsAndImmutableHistory()
    {
        var database = await postgres.CreateDatabaseAsync();
        var seeded = await SeedTwoOperatorsAndZonesAsync(database);

        await using var db = database.CreateDbContext();
        var zone = await db.ZonasCobertura.Include(x => x.Franjas).SingleAsync(x => x.Id == seeded.ZoneA1);
        Assert.Equal(EstadoZonaCobertura.Activa, zone.Estado);
        Assert.Equal(4326, zone.Geometria.SRID);
        Assert.Single(zone.Franjas);

        var revisions = await db.ZonasCoberturaRevisiones.AsNoTracking()
            .Where(x => x.ZonaId == seeded.ZoneA1).OrderBy(x => x.NumeroRevision).ToListAsync();
        Assert.Equal(3, revisions.Count);
        using var initial = JsonDocument.Parse(revisions[0].SnapshotJson);
        using var current = JsonDocument.Parse(revisions[^1].SnapshotJson);
        Assert.Equal("MultiPolygon", current.RootElement.GetProperty("geometria").GetProperty("type").GetString());
        Assert.Empty(initial.RootElement.GetProperty("franjas").EnumerateArray());
        Assert.Single(current.RootElement.GetProperty("franjas").EnumerateArray());

        var slot = zone.Franjas.Single();
        zone.CambiarFranja(slot.Id, slot.DiaSemanaIso,
            new TimeOnly(9, 0).Add(TimeSpan.FromTicks(1_234_560)),
            new TimeOnly(10, 0).Add(TimeSpan.FromTicks(6_543_210)), slot.Estado, zone.Version);
        await new EfZonasCoberturaRepository(db).GuardarCambioAsync(zone, zone.Version - 1, ActorA);
        var fractionalSnapshot = await db.ZonasCoberturaRevisiones.AsNoTracking()
            .Where(x => x.ZonaId == seeded.ZoneA1).OrderBy(x => x.NumeroRevision)
            .Select(x => x.SnapshotJson).LastAsync();
        using var fractional = JsonDocument.Parse(fractionalSnapshot);
        var fractionalSlot = fractional.RootElement.GetProperty("franjas").EnumerateArray().Single();
        Assert.Equal("09:00:00.123456", fractionalSlot.GetProperty("horaInicio").GetString());
        Assert.Equal("10:00:00.654321", fractionalSlot.GetProperty("horaFin").GetString());

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE public.\"ZonasCoberturaRevisiones\" SET \"Version\" = \"Version\" WHERE \"ZonaId\" = @id", connection);
        command.Parameters.AddWithValue("id", seeded.ZoneA1);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState, exception.SqlState);
    }

    [Fact]
    public async Task NewTablesForceRlsUseSchemaOwnerAndDoNotGrantWorkersOrRuntimeMutationOfHistory()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT c.relname, c.relrowsecurity, c.relforcerowsecurity, pg_get_userbyid(c.relowner),
                   has_table_privilege('nexoruta_worker', c.oid, 'SELECT'),
                   has_table_privilege('nexoruta_api', c.oid, 'SELECT'),
                   has_table_privilege('nexoruta_api', c.oid, 'INSERT'),
                   has_table_privilege('nexoruta_api', c.oid, 'UPDATE'),
                   has_table_privilege('nexoruta_api', c.oid, 'DELETE')
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relname = ANY(@tables)
            ORDER BY c.relname
            """, connection);
        command.Parameters.AddWithValue("tables", new[]
            { "DepartamentosReferencia", "FranjasEntrega", "ZonasCobertura", "ZonasCoberturaRevisiones" });

        await using var reader = await command.ExecuteReaderAsync();
        var rows = new Dictionary<string, (bool Rls, bool Force, string Owner, bool WorkerRead, bool ApiRead, bool ApiInsert, bool ApiUpdate, bool ApiDelete)>();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0), (reader.GetBoolean(1), reader.GetBoolean(2), reader.GetString(3),
                reader.GetBoolean(4), reader.GetBoolean(5), reader.GetBoolean(6), reader.GetBoolean(7), reader.GetBoolean(8)));
        }

        Assert.Equal(4, rows.Count);
        foreach (var table in rows.Values)
        {
            Assert.True(table.Rls);
            Assert.True(table.Force);
            Assert.Equal("nexoruta_schema_owner", table.Owner);
            Assert.False(table.WorkerRead);
        }
        Assert.False(rows["ZonasCoberturaRevisiones"].ApiUpdate);
        Assert.False(rows["DepartamentosReferencia"].ApiInsert);
        foreach (var table in rows.Values) Assert.False(table.ApiDelete);
        foreach (var tableName in new[] { "ZonasCobertura", "FranjasEntrega" })
        {
            Assert.True(rows[tableName].ApiRead);
            Assert.True(rows[tableName].ApiInsert);
            Assert.True(rows[tableName].ApiUpdate);
        }
        Assert.True(rows["ZonasCoberturaRevisiones"].ApiRead);
        Assert.True(rows["ZonasCoberturaRevisiones"].ApiInsert);
        Assert.True(rows["DepartamentosReferencia"].ApiRead);
    }

    [Fact]
    public async Task SpatialQueryUsesCoversReturnsAllSameOperatorMatchesAndDoesNotLeakOtherOperator()
    {
        var database = await postgres.CreateDatabaseAsync();
        var seeded = await SeedTwoOperatorsAndZonesAsync(database);
        var api = await CreateRestrictedApiConnectionAsync(database);
        var actor = new RlsActorContext(RlsActorKind.Operador, UserId: ActorA, OrganizationId: seeded.OperatorA);

        var interior = await QueryAsAsync(api, actor, -55.5, -33.5);
        var edge = await QueryAsAsync(api, actor, -56, -33.5);
        var exterior = await QueryAsAsync(api, actor, -57, -33.5);

        Assert.Equal(new[] { seeded.ZoneA1, seeded.ZoneA2 }.Order(), interior.Select(x => x.Id).Order());
        Assert.Equal(new[] { seeded.ZoneA1, seeded.ZoneA2 }.Order(), edge.Select(x => x.Id).Order());
        Assert.Empty(exterior);
        Assert.DoesNotContain(interior, x => x.OperadorId == seeded.OperatorB);
    }

    [Fact]
    public async Task RlsAllowsOnlyOperatorContextAndDeniesOtherActorKindsAndNoContext()
    {
        var database = await postgres.CreateDatabaseAsync();
        var seeded = await SeedTwoOperatorsAndZonesAsync(database);
        var api = await CreateRestrictedApiConnectionAsync(database);

        var own = await CountAsAsync(api, new RlsActorContext(RlsActorKind.Operador, UserId: ActorA, OrganizationId: seeded.OperatorA));
        var other = await CountAsAsync(api, new RlsActorContext(RlsActorKind.Operador, UserId: ActorB, OrganizationId: seeded.OperatorB));
        var commerce = await CountAsAsync(api, new RlsActorContext(RlsActorKind.Comercio, OrganizationId: seeded.OperatorA));
        var platform = await CountAsAsync(api, new RlsActorContext(RlsActorKind.Plataforma));
        var identity = await CountAsAsync(api, new RlsActorContext(RlsActorKind.Identidad));
        var absent = await CountAsAsync(api, null);

        Assert.Equal(2, own);
        Assert.Equal(1, other);
        Assert.Equal(0, commerce);
        Assert.Equal(0, platform);
        Assert.Equal(0, identity);
        Assert.Equal(0, absent);
    }

    [Fact]
    public async Task RlsRejectsCrossOperatorWritesAndDatabaseRejectsCrossOperatorSlotForeignKey()
    {
        var database = await postgres.CreateDatabaseAsync();
        var seeded = await SeedTwoOperatorsAndZonesAsync(database);
        var api = await CreateRestrictedApiConnectionAsync(database);
        var wrongTenantZone = ZonaCobertura.Crear(seeded.OperatorB, "falsificación", CreatePolygon());

        await using (var db = RestrictedDb(api))
        await using (var lease = await db.BeginOrJoinAsync())
        {
            await db.ApplyAsync(new RlsActorContext(RlsActorKind.Operador, UserId: ActorA, OrganizationId: seeded.OperatorA));
            var repository = new EfZonasCoberturaRepository(db);
            var denied = await Assert.ThrowsAsync<DbUpdateException>(() => repository.GuardarNuevaAsync(wrongTenantZone, ActorA));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ((PostgresException)denied.InnerException!).SqlState);
        }

        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                INSERT INTO public."FranjasEntrega" ("Id", "OperadorId", "ZonaId", "DiaSemanaIso", "HoraInicio", "HoraFin", "Estado")
                VALUES (@id, @operator, @zone, 1, TIME '09:00', TIME '10:00', 'Activa')
                """, connection);
            command.Parameters.AddWithValue("id", Guid.CreateVersion7());
            command.Parameters.AddWithValue("operator", seeded.OperatorB);
            command.Parameters.AddWithValue("zone", seeded.ZoneA1);
            var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
        }
    }

    [Fact]
    public async Task RestrictedApiCanCreateOnlyTheAuthenticatedOperatorsConfiguration()
    {
        var database = await postgres.CreateDatabaseAsync();
        var seeded = await SeedTwoOperatorsAndZonesAsync(database);
        var api = await CreateRestrictedApiConnectionAsync(database);
        var zone = ZonaCobertura.Crear(seeded.OperatorA, "API coverage", CreatePolygon(), franjasIniciales:
        [new(DiaSemanaIso.Lunes, new TimeOnly(9, 0), new TimeOnly(10, 0))]);

        await using (var db = RestrictedDb(api))
        await using (var lease = await db.BeginOrJoinAsync())
        {
            await db.ApplyAsync(new RlsActorContext(RlsActorKind.Operador, UserId: ActorA, OrganizationId: seeded.OperatorA));
            await new EfZonasCoberturaRepository(db).GuardarNuevaAsync(zone, ActorA);
            await lease.CommitAsync();
        }

        await using var verify = database.CreateDbContext();
        var saved = await verify.ZonasCobertura.Include(x => x.Franjas).SingleAsync(x => x.Id == zone.Id);
        Assert.Equal(seeded.OperatorA, saved.OperadorId);
        Assert.Equal(EstadoZonaCobertura.Inactiva, saved.Estado);
        Assert.Single(saved.Franjas);
        Assert.Single(await verify.ZonasCoberturaRevisiones.Where(x => x.ZonaId == zone.Id).ToListAsync());
    }

    [Fact]
    public async Task ConcurrentStaleEditRollsBackWithoutCreatingMixedRevision()
    {
        var database = await postgres.CreateDatabaseAsync();
        var seeded = await SeedTwoOperatorsAndZonesAsync(database);
        await using var first = database.CreateDbContext();
        await using var second = database.CreateDbContext();
        var repositoryFirst = new EfZonasCoberturaRepository(first);
        var repositorySecond = new EfZonasCoberturaRepository(second);
        var zoneFirst = await repositoryFirst.ObtenerAsync(seeded.ZoneA1);
        var zoneSecond = await repositorySecond.ObtenerAsync(seeded.ZoneA1);
        Assert.NotNull(zoneFirst);
        Assert.NotNull(zoneSecond);

        zoneFirst.CambiarNombre("Centro edición A", zoneFirst.Version);
        zoneSecond.CambiarNombre("Centro edición B", zoneSecond.Version);
        await repositoryFirst.GuardarCambioAsync(zoneFirst, 2, ActorA);
        await Assert.ThrowsAsync<ConflictoConcurrenciaZonaException>(
            () => repositorySecond.GuardarCambioAsync(zoneSecond, 2, ActorA));

        await using var verify = database.CreateDbContext();
        var revisions = await verify.ZonasCoberturaRevisiones.AsNoTracking()
            .Where(x => x.ZonaId == seeded.ZoneA1).OrderBy(x => x.NumeroRevision).ToListAsync();
        Assert.Equal(4, revisions.Count);
        using var last = JsonDocument.Parse(revisions[^1].SnapshotJson);
        Assert.Equal("Centro edición A", last.RootElement.GetProperty("nombre").GetString());
        Assert.Single(last.RootElement.GetProperty("franjas").EnumerateArray());
    }

    [Fact]
    public async Task ApiCannotUpdateOrDeleteRevisionAndDepartmentReferenceIsReadOnly()
    {
        var database = await postgres.CreateDatabaseAsync();
        var seeded = await SeedTwoOperatorsAndZonesAsync(database);
        var api = await CreateRestrictedApiConnectionAsync(database);
        await using (var seed = database.CreateDbContext())
        {
            seed.DepartamentosReferencia.Add(new DepartamentoReferencia("UY-MO", "v1", "Montevideo", CreatePolygon(),
                "https://example.invalid", "test attribution", "test license", DateTimeOffset.UtcNow));
            await seed.SaveChangesAsync();
        }

        var referenceVisibleToOperator = await CountDepartmentsAsAsync(api,
            new RlsActorContext(RlsActorKind.Operador, UserId: ActorA, OrganizationId: seeded.OperatorA));
        var referenceHiddenFromCommerce = await CountDepartmentsAsAsync(api,
            new RlsActorContext(RlsActorKind.Comercio, OrganizationId: seeded.OperatorA));
        Assert.Equal(1, referenceVisibleToOperator);
        Assert.Equal(0, referenceHiddenFromCommerce);

        await using var connection = new NpgsqlConnection(api);
        await connection.OpenAsync();

        await using var delete = new NpgsqlCommand("DELETE FROM public.\"ZonasCoberturaRevisiones\" WHERE \"ZonaId\" = @id", connection);
        delete.Parameters.AddWithValue("id", seeded.ZoneA1);
        var deleteError = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, deleteError.SqlState);

        await using var insertReference = new NpgsqlCommand("""
            INSERT INTO public."DepartamentosReferencia" ("Codigo", "VersionFuente", "Nombre", "Geometria", "UrlFuente", "Atribucion", "Licencia", "ImportadaUtc")
            VALUES ('UY-MO', 'v1', 'Montevideo', ST_Multi(ST_GeomFromText('POLYGON((-56 -35,-55 -35,-55 -34,-56 -35))', 4326)), 'https://example.invalid', 'test', 'test', now())
            """, connection);
        var insertError = await Assert.ThrowsAsync<PostgresException>(() => insertReference.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, insertError.SqlState);
    }

    private static async Task<IReadOnlyList<ZonaCobertura>> QueryAsAsync(string connectionString, RlsActorContext actor, double longitude, double latitude)
    {
        await using var db = RestrictedDb(connectionString);
        await using var lease = await db.BeginOrJoinAsync();
        await db.ApplyAsync(actor);
        var result = await new EfZonasCoberturaRepository(db).ConsultarCoberturaAsync(longitude, latitude);
        await lease.CommitAsync();
        return result;
    }

    private static async Task<int> CountAsAsync(string connectionString, RlsActorContext? actor)
    {
        await using var db = RestrictedDb(connectionString);
        await using var lease = await db.BeginOrJoinAsync();
        if (actor is not null) await db.ApplyAsync(actor);
        var count = await db.ZonasCobertura.CountAsync();
        await lease.CommitAsync();
        return count;
    }

    private static async Task<int> CountDepartmentsAsAsync(string connectionString, RlsActorContext actor)
    {
        await using var db = RestrictedDb(connectionString);
        await using var lease = await db.BeginOrJoinAsync();
        await db.ApplyAsync(actor);
        var count = await db.DepartamentosReferencia.CountAsync();
        await lease.CommitAsync();
        return count;
    }

    private static NexoRutaDbContext RestrictedDb(string connectionString) =>
        new(new DbContextOptionsBuilder<NexoRutaDbContext>().UseNexoRutaPostgres(connectionString).Options);

    private static async Task<string> CreateRestrictedApiConnectionAsync(IntegrationDatabase database)
    {
        var role = $"cu02_api_{Guid.NewGuid():N}";
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE ROLE \"{role}\" LOGIN INHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS PASSWORD '{password}'", connection))
            await create.ExecuteNonQueryAsync();
        await using (var grant = new NpgsqlCommand($"GRANT nexoruta_api TO \"{role}\"", connection))
            await grant.ExecuteNonQueryAsync();
        var builder = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Username = role,
            Password = password,
            Pooling = false
        };
        return builder.ConnectionString;
    }

    private static async Task<SeededZones> SeedTwoOperatorsAndZonesAsync(IntegrationDatabase database)
    {
        await using var db = database.CreateDbContext();
        var operatorA = new Operador("Operador A");
        var operatorB = new Operador("Operador B");
        db.Operadores.AddRange(operatorA, operatorB);
        await db.SaveChangesAsync();

        var repository = new EfZonasCoberturaRepository(db);
        var first = await CreateActiveZoneAsync(repository, operatorA.Id, "A zona uno");
        var second = await CreateActiveZoneAsync(repository, operatorA.Id, "A zona dos");
        var other = await CreateActiveZoneAsync(repository, operatorB.Id, "B zona");
        return new(operatorA.Id, operatorB.Id, first, second, other);
    }

    private static async Task<Guid> CreateActiveZoneAsync(EfZonasCoberturaRepository repository, Guid operatorId, string name)
    {
        var zone = ZonaCobertura.Crear(operatorId, name, CreatePolygon());
        await repository.GuardarNuevaAsync(zone, ActorA);
        zone = (await repository.ObtenerAsync(zone.Id))!;
        zone.AgregarFranja(DiaSemanaIso.Lunes, new TimeOnly(9, 0), new TimeOnly(10, 0), zone.Version);
        await repository.GuardarCambioAsync(zone, 0, ActorA);
        zone.Activar(zone.Version);
        await repository.GuardarCambioAsync(zone, 1, ActorA);
        return zone.Id;
    }

    private static NetTopologySuite.Geometries.Polygon CreatePolygon()
    {
        var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        return factory.CreatePolygon(factory.CreateLinearRing(
        [
            new(-56, -34), new(-55, -34), new(-55, -33), new(-56, -33), new(-56, -34)
        ]));
    }

    private sealed record SeededZones(Guid OperatorA, Guid OperatorB, Guid ZoneA1, Guid ZoneA2, Guid ZoneB);
}
