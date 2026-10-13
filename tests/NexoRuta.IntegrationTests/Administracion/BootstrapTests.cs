using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Administracion;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.IntegrationTests.Fixtures;

namespace NexoRuta.IntegrationTests.Administracion;

public sealed class BootstrapTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public async Task ArbitraryRoleCatalogIsCreateOnlyAndIdempotent(int count)
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        var initial = new AccesoInicial("owner@test.local", "Operator", "Commerce", "operator@test.local")
            { OperadorId = Guid.NewGuid(), ComercioId = Guid.NewGuid() };
        var settings = AuthenticationTestData.Settings();
        for (var i = 0; i < count; i++)
        {
            settings[$"Bootstrap:Roles:{i}:Codigo"] = $"custom_{i}";
            settings[$"Bootstrap:Roles:{i}:Nombre"] = $"Custom {i}";
            settings[$"Bootstrap:Roles:{i}:ComercioId"] = initial.ComercioId.ToString();
            settings[$"Bootstrap:Roles:{i}:Permisos:0"] = "envios.consultar";
        }
        var seeder = new DatosInicialesSeeder(db, initial,
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), new PasswordHasher<string>());
        await seeder.SeedAsync();
        Assert.Equal(count, await db.Roles.CountAsync());
        Assert.All(await db.AccesosUsuario.ToListAsync(), access => Assert.Null(access.RolId));
        if (count > 0)
        {
            var role = await db.Roles.OrderBy(x => x.Codigo).FirstAsync();
            role.Renombrar("Administrator-edited name");
            db.RemoveRange(await db.RolesPermisos.ToListAsync());
            await db.SaveChangesAsync();
        }
        db.Entry(await db.Comercios.SingleAsync()).Property(x => x.Nombre).CurrentValue = "Edited commerce";
        await db.SaveChangesAsync();
        await seeder.SeedAsync();
        Assert.Equal(count, await db.Roles.CountAsync());
        Assert.Empty(await db.RolesPermisos.ToListAsync());
        Assert.Equal("Edited commerce", (await db.Comercios.SingleAsync()).Nombre);
        if (count > 0)
        {
            Assert.Contains(await db.Roles.ToListAsync(), x => x.Nombre == "Administrator-edited name");
            db.RemoveRange(await db.Roles.ToListAsync());
            await db.SaveChangesAsync();
            await seeder.SeedAsync();
            Assert.Empty(await db.Roles.ToListAsync()); // Administrative deletion must not recreate initial roles.
        }
        Assert.Equal(2, await db.Usuarios.CountAsync());
        Assert.Equal(2, await db.AccesosUsuario.CountAsync());
    }

    [Fact]
    public async Task ExistingAssignmentsAndHashesSurviveRepeatedSeed()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        var initial = new AccesoInicial("owner@test.local", "Operator", "Commerce", "operator@test.local");
        var seeder = AuthenticationTestData.Seeder(db, initial);
        await seeder.SeedAsync();
        var access = await db.AccesosUsuario.SingleAsync(x => x.ComercioId != null);
        var role = new Rol("arbitrary", "Arbitrary", null, access.ComercioId);
        db.Add(role);
        access.AsignarRol(role);
        var user = await db.Usuarios.SingleAsync(x => x.Id == access.UsuarioId);
        var hash = AuthenticationTestData.Hash(user.Email);
        user.ActualizarPasswordHash(hash);
        await db.SaveChangesAsync();
        await seeder.SeedAsync();
        Assert.Equal(role.Id, access.RolId);
        Assert.Equal(hash, user.PasswordHash);
        Assert.Empty(await db.RolesPermisos.Where(x => x.RolId == role.Id).ToListAsync());
    }

    [Fact]
    public async Task SnapshotMatchesTheRuntimeModel()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
