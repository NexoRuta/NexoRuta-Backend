using Microsoft.EntityFrameworkCore;
using NexoRuta.Domain.Administracion;
using NexoRuta.IntegrationTests.Fixtures;

namespace NexoRuta.IntegrationTests.Administracion;

public sealed class AP04PermissionMigrationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string PreviousMigration = "20261108020000_CommerceSettlements";

    [Fact]
    public async Task MigrationGrantsReturnPermissionToExistingProtectedCommerceOwner()
    {
        var database = await postgres.CreateDatabaseAsync(PreviousMigration);
        const string email = "ap04-existing-commerce-owner@example.test";
        Guid commerceId;
        Guid roleId;
        Guid accessId;
        await using (var before = database.CreateDbContext())
        {
            var commerce = new Comercio("AP04 existing commerce");
            var role = new Rol("propietario_comercio", "Commerce owner", null, commerce.Id);
            role.ProtegerComoRolSistema();
            var user = new Usuario(email, AuthenticationTestData.Hash(email));
            var access = new AccesoUsuario(user.Id, null, commerce.Id, role, esPropietario: true);
            before.AddRange(commerce, role, user, access);
            await before.SaveChangesAsync();

            commerceId = commerce.Id;
            roleId = role.Id;
            accessId = access.Id;
            Assert.False(await before.Permisos.AnyAsync(permission => permission.Codigo == "devoluciones.gestionar"));
        }

        await using (var upgrade = database.CreateDbContext())
            await upgrade.Database.MigrateAsync();

        await using var verify = database.CreateDbContext();
        var permission = await verify.Permisos.SingleAsync(item => item.Codigo == "devoluciones.gestionar");
        Assert.True(await verify.Comercios.AnyAsync(item => item.Id == commerceId));
        Assert.True((await verify.Roles.SingleAsync(item => item.Id == roleId)).EsProtegidoSistema);
        Assert.True((await verify.AccesosUsuario.SingleAsync(item => item.Id == accessId)).EsPropietario);
        Assert.True(await verify.RolesPermisos.AnyAsync(grant => grant.RolId == roleId && grant.PermisoId == permission.Id));
    }
}
