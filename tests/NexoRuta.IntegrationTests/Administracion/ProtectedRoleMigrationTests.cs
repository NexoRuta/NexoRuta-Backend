using Microsoft.EntityFrameworkCore;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.IntegrationTests.Fixtures;

namespace NexoRuta.IntegrationTests.Administracion;

public sealed class ProtectedRoleMigrationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
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
                new RolPermiso(role.Id, permission.Id), access);
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
