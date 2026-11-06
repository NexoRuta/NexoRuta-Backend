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
        var database = await postgres.CreateDatabaseAsync("20261015212134_AddVersionSesionAcceso");
        Guid operatorId;
        Guid userId;
        Guid roleId;
        Guid permissionId;
        Guid accessId;
        await using (var db = database.CreateDbContext())
        {
            operatorId = Guid.NewGuid();
            userId = Guid.NewGuid(); roleId = Guid.NewGuid(); permissionId = Guid.NewGuid(); accessId = Guid.NewGuid();
            const string email = "migration-compatibility@test.local";
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Operadores" ("Id","Nombre") VALUES ({operatorId},'Operator');
                INSERT INTO "Usuarios" ("Id","Email","PasswordHash") VALUES ({userId},{email},{AuthenticationTestData.Hash(email)});
                INSERT INTO "Roles" ("Id","Codigo","Nombre","OperadorId","TipoOrganizacion") VALUES ({roleId},'administrador_operador','Operator administrator',{operatorId},'Operador');
                INSERT INTO "Permisos" ("Id","Codigo","Nombre") VALUES ({permissionId},'usuarios.gestionar','Manage users');
                INSERT INTO "RolesPermisos" ("RolId","PermisoId") VALUES ({roleId},{permissionId});
                INSERT INTO "AccesosUsuario" ("Id","UsuarioId","OperadorId","RolId") VALUES ({accessId},{userId},{operatorId},{roleId});
                """);
        }

        await using (var upgraded = database.CreateDbContext())
        {
            await upgraded.Database.MigrateAsync();
            Assert.Equal(userId, (await upgraded.Usuarios.SingleAsync()).Id);
            Assert.Equal(accessId, (await upgraded.AccesosUsuario.SingleAsync()).Id);
            var role = await upgraded.Roles.SingleAsync(r => r.Id == roleId);
            Assert.Equal(roleId, role.Id);
            Assert.True(role.EsProtegidoSistema);
            Assert.Equal(permissionId, await upgraded.RolesPermisos.Where(x => x.RolId == roleId).Select(x => x.PermisoId).SingleAsync());
            Assert.Equal(operatorId, role.OperadorId);
        }
    }
}
