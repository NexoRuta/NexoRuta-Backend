using Microsoft.EntityFrameworkCore;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.IntegrationTests.Fixtures;

namespace NexoRuta.IntegrationTests.Administracion;

public sealed class ProtectedRoleMigrationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task LossConfirmationMigrationProvisionsDispatcherForExistingOperatorsWithoutAssigningUsers()
    {
        var database = await postgres.CreateDatabaseAsync("20261026123301_DecisionOperativaDevolucion");
        Guid operatorId;
        await using (var before = database.CreateDbContext())
        {
            var operatorEntity = new Operador("Existing operator before dispatcher role");
            operatorId = operatorEntity.Id;
            before.Operadores.Add(operatorEntity);
            await before.SaveChangesAsync();
        }

        await using (var upgrade = database.CreateDbContext())
            await upgrade.Database.MigrateAsync();

        await using var verify = database.CreateDbContext();
        var dispatcher = await verify.Roles.SingleAsync(x => x.OperadorId == operatorId
            && x.Codigo == "despachador_operador");
        Assert.True(dispatcher.EsProtegidoSistema);
        var codes = await (from grant in verify.RolesPermisos
                           join permission in verify.Permisos on grant.PermisoId equals permission.Id
                           where grant.RolId == dispatcher.Id
                           select permission.Codigo).ToArrayAsync();
        Assert.Contains("envios.gestionar", codes);
        Assert.Contains("envios.consultar", codes);
        Assert.False(await verify.AccesosUsuario.AnyAsync(x => x.RolId == dispatcher.Id));
    }

    [Fact]
    public async Task AdditiveClassificationPreservesExistingOperatorIdentityAndRoleRelationships()
    {
        var database = await postgres.CreateDatabaseAsync();
        Guid operatorId;
        Guid userId;
        Guid roleId;
        Guid permissionId;
        Guid accessId;
        await using (var db = database.CreateDbContext())
        {
            operatorId = Guid.NewGuid();
            var user = new Usuario("migration-compatibility@test.local", AuthenticationTestData.Hash("migration-compatibility@test.local"));
            var role = new Rol("administrador_operador", "Operator administrator", operatorId, null);
            var permission = new Permiso("usuarios.gestionar", "Manage users");
            var access = new AccesoUsuario(user.Id, operatorId, null, role);
            db.AddRange(new Operador("Operator", operatorId), user, role, permission,
                new RolPermiso(role.Id, permission.Id));
            await db.SaveChangesAsync();
            db.AccesosUsuario.Add(access);
            await db.SaveChangesAsync();
            userId = user.Id;
            roleId = role.Id;
            permissionId = permission.Id;
            accessId = access.Id;
        }

        // Reproduce an existing installation at the last pre-classification schema, retaining its rows.
        await using (var db = database.CreateDbContext())
            await db.Database.MigrateAsync("20261015212134_AddVersionSesionAcceso");

        await using (var upgraded = database.CreateDbContext())
        {
            await upgraded.Database.MigrateAsync();
            Assert.Equal(userId, (await upgraded.Usuarios.SingleAsync()).Id);
            Assert.Equal(accessId, (await upgraded.AccesosUsuario.SingleAsync()).Id);
            var role = await upgraded.Roles.SingleAsync();
            Assert.Equal(roleId, role.Id);
            Assert.True(role.EsProtegidoSistema);
            Assert.Equal(permissionId, await upgraded.RolesPermisos.Select(x => x.PermisoId).SingleAsync());
            Assert.Equal(operatorId, role.OperadorId);
        }
    }
}
