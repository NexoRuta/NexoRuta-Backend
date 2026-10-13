using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Administracion.Repositories;
using NexoRuta.Infrastructure.Administracion.Autenticacion;
using NexoRuta.IntegrationTests.Fixtures;
using Npgsql;

namespace NexoRuta.IntegrationTests.Administracion;

public sealed class Cu01MigrationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedRolesConvertPerOrganizationWithExactEffectiveGrants(bool formerlyAppliedSchema)
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261012230124_AddUsuarioPasswordHash");
        var roleId = Guid.NewGuid();
        var permissionId = Guid.NewGuid();
        var commerceIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var userIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var accessIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"Roles\" (\"Id\", \"Codigo\", \"Nombre\", \"TipoOrganizacion\") VALUES ({roleId}, 'dispatch', 'Dispatch', 'Comercio')");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"Permisos\" (\"Id\", \"Codigo\", \"Nombre\") VALUES ({permissionId}, 'envios.consultar', 'View shipments')");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"RolesPermisos\" (\"RolId\", \"PermisoId\") VALUES ({roleId}, {permissionId})");
        for (var i = 0; i < 2; i++)
        {
            var email = $"legacy{i}@test.local";
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"Comercios\" (\"Id\", \"Nombre\") VALUES ({commerceIds[i]}, 'Edited historical organization')");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"Usuarios\" (\"Id\", \"Email\", \"PasswordHash\") VALUES ({userIds[i]}, {email}, {AuthenticationTestData.Hash(email)})");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"AccesosUsuario\" (\"Id\", \"UsuarioId\", \"ComercioId\", \"RolId\", \"EsPropietario\") VALUES ({accessIds[i]}, {userIds[i]}, {commerceIds[i]}, {roleId}, true)");
        }
        if (formerlyAppliedSchema)
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"AccesosUsuario\" DROP CONSTRAINT \"CK_AccesosUsuario_Propietario\"; ALTER TABLE \"AccesosUsuario\" DROP COLUMN \"EsPropietario\"; ALTER TABLE \"AccesosUsuario\" ALTER COLUMN \"RolId\" SET NOT NULL; ALTER TABLE \"Usuarios\" ALTER COLUMN \"PasswordHash\" SET NOT NULL;");
        }
        await migrator.MigrateAsync();
        var roles = await db.Roles.ToListAsync();
        Assert.Equal(2, roles.Count);
        Assert.Contains(roles, r => r.Id == roleId);
        Assert.Equal(2, await db.RolesPermisos.CountAsync());
        var permissions = new EfPermisosUsuarioRepository(db);
        for (var i = 0; i < 2; i++)
        {
            var access = await db.AccesosUsuario.SingleAsync(x => x.Id == accessIds[i]);
            Assert.Equal(userIds[i], access.UsuarioId);
            Assert.Equal(commerceIds[i], access.ComercioId);
            Assert.Equal(commerceIds[i], roles.Single(x => x.Id == access.RolId).ComercioId);
            Assert.True(await permissions.TienePermisoAsync(userIds[i], accessIds[i], "envios.consultar"));
            if (!formerlyAppliedSchema) Assert.True(db.Entry(access).Property<bool?>("EsPropietario").CurrentValue);
            else Assert.Null(db.Entry(access).Property<bool?>("EsPropietario").CurrentValue);
        }
        var firstRole = (await db.AccesosUsuario.SingleAsync(x => x.Id == accessIds[0])).RolId;
        db.Remove(await db.RolesPermisos.SingleAsync(x => x.RolId == firstRole));
        await db.SaveChangesAsync();
        Assert.False(await permissions.TienePermisoAsync(userIds[0], accessIds[0], "envios.consultar"));
        Assert.True(await permissions.TienePermisoAsync(userIds[1], accessIds[1], "envios.consultar"));
        // Shared code cannot be reintroduced by unsafe rollback that merges administrative changes.
        var rollback = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync("20261012230124_AddUsuarioPasswordHash"));
        Assert.Contains("explicit consolidation", rollback.MessageText);
        Assert.Equal(2, await db.Roles.CountAsync());
        Assert.Equal(1, await db.RolesPermisos.CountAsync());
    }

    [Fact]
    public async Task DatabaseRejectsCrossOrganizationAndCrossKindAssignmentsEvenWhenIdsMatch()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        var sharedId = Guid.NewGuid();
        var op = new Operador("Operator", sharedId);
        var commerce = new Comercio("Commerce", sharedId);
        var other = new Comercio("Other commerce");
        var user = new Usuario("user@test.local", AuthenticationTestData.Hash("user@test.local"));
        var role = new Rol("dispatch", "Dispatch", op.Id, null);
        var access = new AccesoUsuario(user.Id, null, commerce.Id);
        db.AddRange(op, commerce, other, user, role, access);
        await db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AccesosUsuario\" SET \"RolId\" = {role.Id} WHERE \"Id\" = {access.Id}"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        var commerceRole = new Rol("dispatch", "Dispatch", null, other.Id);
        db.Add(commerceRole);
        await db.SaveChangesAsync();
        error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AccesosUsuario\" SET \"RolId\" = {commerceRole.Id} WHERE \"Id\" = {access.Id}"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Roles\" SET \"ComercioId\" = {commerce.Id} WHERE \"Id\" = {role.Id}"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Null(await new EfUsuariosLoginRepository(db).ObtenerPorEmailAsync(user.Email));
        Assert.Null(await new EfAccesosUsuarioRepository(db).ObtenerAsync(access.Id));
    }
}
