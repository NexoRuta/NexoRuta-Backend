using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.IntegrationTests.Fixtures;
using Npgsql;

namespace NexoRuta.IntegrationTests.Administracion;

public sealed class OrganizacionPropiedadMigrationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task UpgradePreservesOnlyUnambiguousLegacyCommerceOwnerEvidence()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261016045116_AddPlatformRolePermissions");

        var knownCommerce = Guid.NewGuid();
        var ambiguousCommerce = Guid.NewGuid();
        var unownedCommerce = Guid.NewGuid();
        var operatorId = Guid.NewGuid();
        var knownUser = Guid.NewGuid();
        var ambiguousUsers = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var unownedUser = Guid.NewGuid();
        var operatorUser = Guid.NewGuid();
        var accessIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        foreach (var (id, name) in new[]
        {
            (knownCommerce, "Known"), (ambiguousCommerce, "Ambiguous"), (unownedCommerce, "Unowned")
        })
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"Comercios\" (\"Id\", \"Nombre\") VALUES ({id}, {name})");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"Operadores\" (\"Id\", \"Nombre\") VALUES ({operatorId}, 'Operator')");

        foreach (var (id, email) in new[]
        {
            (knownUser, "known@test.local"),
            (ambiguousUsers[0], "ambiguous1@test.local"),
            (ambiguousUsers[1], "ambiguous2@test.local"),
            (unownedUser, "unowned@test.local"),
            (operatorUser, "operator@test.local")
        })
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"Usuarios\" (\"Id\", \"Email\") VALUES ({id}, {email})");

        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"AccesosUsuario\" (\"Id\", \"UsuarioId\", \"ComercioId\", \"EsPropietario\") VALUES ({accessIds[0]}, {knownUser}, {knownCommerce}, true)");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"AccesosUsuario\" (\"Id\", \"UsuarioId\", \"ComercioId\", \"EsPropietario\") VALUES ({accessIds[1]}, {ambiguousUsers[0]}, {ambiguousCommerce}, true)");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"AccesosUsuario\" (\"Id\", \"UsuarioId\", \"ComercioId\", \"EsPropietario\") VALUES ({accessIds[2]}, {ambiguousUsers[1]}, {ambiguousCommerce}, true)");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"AccesosUsuario\" (\"Id\", \"UsuarioId\", \"ComercioId\", \"EsPropietario\") VALUES ({accessIds[3]}, {unownedUser}, {unownedCommerce}, false)");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"AccesosUsuario\" (\"Id\", \"UsuarioId\", \"OperadorId\", \"EsPropietario\") VALUES ({accessIds[4]}, {operatorUser}, {operatorId}, false)");

        await migrator.MigrateAsync();
        db.ChangeTracker.Clear();

        var accesses = await db.AccesosUsuario.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(accessIds.Order(), accesses.Select(x => x.Id).Order());
        Assert.True(accesses.Single(x => x.Id == accessIds[0]).EsPropietario);
        Assert.All(accesses.Where(x => accessIds[1..3].Contains(x.Id)), x => Assert.False(x.EsPropietario));
        Assert.False(accesses.Single(x => x.Id == accessIds[3]).EsPropietario);
        Assert.False(accesses.Single(x => x.Id == accessIds[4]).EsPropietario);
        Assert.Equal(5, await db.Usuarios.CountAsync());
        Assert.Equal(3, await db.Comercios.CountAsync());
        Assert.Single(await db.Operadores.ToListAsync());

        var duplicateOwner = new Usuario("duplicate-owner@test.local", "hash");
        db.AddRange(duplicateOwner, new AccesoUsuario(duplicateOwner.Id, null, knownCommerce, esPropietario: true));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        db.ChangeTracker.Clear();
        Assert.False(await db.Usuarios.AnyAsync(x => x.Email == duplicateOwner.Email));
    }
}
