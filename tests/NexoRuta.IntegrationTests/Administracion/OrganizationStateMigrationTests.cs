using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NexoRuta.Domain.Administracion;
using NexoRuta.IntegrationTests.Fixtures;
using Npgsql;

namespace NexoRuta.IntegrationTests.Administracion;

public sealed class OrganizationStateMigrationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task UpgradePreservesMembershipAndSetsExistingOrganizationsActive()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261016202513_UpdateOrganizationNameConcurrency");

        var commerceId = Guid.CreateVersion7();
        var operatorId = Guid.CreateVersion7();
        var commerceUserId = Guid.CreateVersion7();
        var operatorUserId = Guid.CreateVersion7();
        var commerceAccessId = Guid.CreateVersion7();
        var operatorAccessId = Guid.CreateVersion7();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"Comercios\" (\"Id\", \"Nombre\") VALUES ({commerceId}, 'Legacy commerce')");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"Operadores\" (\"Id\", \"Nombre\") VALUES ({operatorId}, 'Legacy operator')");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"Usuarios\" (\"Id\", \"Email\") VALUES ({commerceUserId}, 'legacy-commerce@test.local')");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"Usuarios\" (\"Id\", \"Email\") VALUES ({operatorUserId}, 'legacy-operator@test.local')");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"AccesosUsuario\" (\"Id\", \"UsuarioId\", \"ComercioId\", \"EsPropietario\") VALUES ({commerceAccessId}, {commerceUserId}, {commerceId}, true)");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"AccesosUsuario\" (\"Id\", \"UsuarioId\", \"OperadorId\", \"EsPropietario\") VALUES ({operatorAccessId}, {operatorUserId}, {operatorId}, true)");

        await migrator.MigrateAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(EstadoOrganizacion.Activa, (await db.Comercios.SingleAsync(x => x.Id == commerceId)).Estado);
        Assert.Equal(EstadoOrganizacion.Activa, (await db.Operadores.SingleAsync(x => x.Id == operatorId)).Estado);
        Assert.Equal(commerceUserId, (await db.AccesosUsuario.SingleAsync(x => x.Id == commerceAccessId)).UsuarioId);
        Assert.Equal(commerceId, (await db.AccesosUsuario.SingleAsync(x => x.Id == commerceAccessId)).ComercioId);
        Assert.True((await db.AccesosUsuario.SingleAsync(x => x.Id == commerceAccessId)).EsPropietario);
        Assert.Equal(operatorUserId, (await db.AccesosUsuario.SingleAsync(x => x.Id == operatorAccessId)).UsuarioId);
        Assert.Equal(operatorId, (await db.AccesosUsuario.SingleAsync(x => x.Id == operatorAccessId)).OperadorId);
        Assert.True((await db.AccesosUsuario.SingleAsync(x => x.Id == operatorAccessId)).EsPropietario);
        Assert.Empty(await db.CambiosEstadoOrganizacion.ToListAsync());
        Assert.Empty(await db.SolicitudesBajaOrganizacion.ToListAsync());
    }

    [Fact]
    public async Task DatabaseAllowsOnlyOnePendingRequestPerOrganization()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        var commerce = new Comercio("Commerce");
        var firstOwner = new Usuario("owner-one@test.local", "hash");
        var secondOwner = new Usuario("owner-two@test.local", "hash");
        var now = DateTimeOffset.UtcNow;
        db.AddRange(commerce, firstOwner, secondOwner);
        db.Add(new SolicitudBajaOrganizacion(TipoAccesoUsuario.Comercio, commerce.Id,
            firstOwner.Id, now, "First reason"));
        await db.SaveChangesAsync();

        db.Add(new SolicitudBajaOrganizacion(TipoAccesoUsuario.Comercio, commerce.Id,
            secondOwner.Id, now.AddMinutes(1), "Second reason"));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation,
            Assert.IsType<PostgresException>(error.InnerException).SqlState);
        db.ChangeTracker.Clear();
        Assert.Single(await db.SolicitudesBajaOrganizacion.ToListAsync());
    }
}
