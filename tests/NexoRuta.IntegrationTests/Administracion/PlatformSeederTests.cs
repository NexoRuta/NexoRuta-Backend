using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NexoRuta.Application.Administracion;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Administracion;
using NexoRuta.Infrastructure.Persistence;

using System.Net;
using Microsoft.EntityFrameworkCore;
using NexoRuta.IntegrationTests.Fixtures;

namespace NexoRuta.IntegrationTests.Administracion;

public sealed class PlatformSeederTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{

    [Fact]
    public async Task NormalApiStartupDoesNotRunTheExplicitPlatformBootstrap()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = new IntegrationApiFactory(database, settings: new Dictionary<string, string?>
        {
            ["DatabaseInitialization:SeedData"] = "false",
            ["PlatformInitialization:Enabled"] = "true",
            ["PlatformInitialization:Email"] = "platform-seed@example.test",
            ["PlatformInitialization:Password"] = AuthenticationTestData.Password
        });
        using var client = factory.CreateHttpClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/live")).StatusCode);
        await using var db = database.CreateDbContext();
        Assert.Empty(await db.AccesosPlataforma.ToListAsync());
        Assert.Empty(await db.Usuarios.ToListAsync());
        Assert.Empty(await db.Operadores.ToListAsync());
        Assert.Empty(await db.Comercios.ToListAsync());
        Assert.Empty(await db.AccesosUsuario.ToListAsync());
        Assert.Empty(await db.Roles.ToListAsync());
    }

    private static DatosInicialesSeeder Seeder(NexoRutaDbContext db, string? email = "platform-seed@example.test",
        string? password = AuthenticationTestData.Password, bool enabled = true, bool synchronizePassword = false) => new(db, new AccesoInicial("unused@example.test", "Unused", "Unused", "Unused"),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PlatformInitialization:Enabled"] = enabled.ToString(),
            ["PlatformInitialization:Email"] = email,
            ["PlatformInitialization:Password"] = password,
            ["PlatformInitialization:SynchronizePassword"] = synchronizePassword.ToString()
        }).Build(), AuthenticationTestData.UserManager(db), AuthenticationTestData.PlatformRepository(db));

    [Fact]
    public async Task RepeatedInitializationRequiresExplicitPasswordSyncAndPreservesProtectedCatalogGrants()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        await Seeder(db).SeedPlataformaAsync();
        var user = await db.Usuarios.AsNoTracking().SingleAsync();
        var access = await db.AccesosPlataforma.AsNoTracking().SingleAsync();
        await Seeder(db).SeedPlataformaAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Seeder(db, password: "Different-valid-password-2026!").SeedPlataformaAsync());
        Assert.DoesNotContain("Different-valid-password-2026!", error.ToString());
        await AuthenticationTestData.PlatformRepository(db)
            .SincronizarPasswordExistenteAsync(user.Email, "Different-valid-password-2026!");
        db.ChangeTracker.Clear();
        var actual = await db.Usuarios.SingleAsync();
        Assert.Equal(user.Id, actual.Id);
        Assert.NotEqual(user.PasswordHash, actual.PasswordHash);
        Assert.Equal(user.VersionCredencial + 1, actual.VersionCredencial);
        Assert.Equal(access.AccesoId, (await db.AccesosPlataforma.SingleAsync()).AccesoId);
        Assert.True((await db.RolesPlataforma.SingleAsync()).EsProtegidoSistema);
        var codes = await (from grant in db.RolesPlataformaPermisos join permission in db.Permisos on grant.PermisoId equals permission.Id select permission.Codigo).ToArrayAsync();
        Assert.Equal(CatalogoPermisos.Plataforma.Select(x => x.Codigo), codes);
        Assert.DoesNotContain(CatalogoPermisos.Todos, x => codes.Contains(x.Codigo));
        var hasher = new PasswordHasher<string>();
        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(actual.Email, actual.PasswordHash!, AuthenticationTestData.Password));
        Assert.Equal(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(actual.Email, actual.PasswordHash!, "Different-valid-password-2026!"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AuthenticationTestData.PlatformRepository(db).InicializarAsync(user.Email, AuthenticationTestData.Password));
    }

    [Fact]
    public async Task ConfiguredPasswordSyncOnlyChangesTheExistingPlatformPasswordWhenExplicitlyEnabled()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        await Seeder(db).SeedPlataformaAsync();
        var original = await db.Usuarios.AsNoTracking().SingleAsync();
        const string rotatedPassword = "Rotated-platform-password-2026!";

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Seeder(db, password: rotatedPassword).SeedPlataformaAsync());
        Assert.Equal(original.PasswordHash, (await db.Usuarios.AsNoTracking().SingleAsync()).PasswordHash);

        await Seeder(db, password: rotatedPassword, synchronizePassword: true).SeedPlataformaAsync();
        db.ChangeTracker.Clear();
        var rotated = await db.Usuarios.AsNoTracking().SingleAsync();
        Assert.Equal(original.Id, rotated.Id);
        Assert.NotEqual(original.PasswordHash, rotated.PasswordHash);
        Assert.Equal(original.VersionCredencial + 1, rotated.VersionCredencial);

        await Seeder(db, password: rotatedPassword, synchronizePassword: true).SeedPlataformaAsync();
        Assert.Equal(rotated.PasswordHash, (await db.Usuarios.AsNoTracking().SingleAsync()).PasswordHash);
        Assert.Equal(rotated.VersionCredencial, (await db.Usuarios.AsNoTracking().SingleAsync()).VersionCredencial);
    }

    [Fact]
    public async Task ConfiguredPasswordSyncCanCreateTheInitialPlatformIdentity()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        await Seeder(db, synchronizePassword: true).SeedPlataformaAsync();

        Assert.Equal("platform-seed@example.test", (await db.Usuarios.SingleAsync()).Email);
        Assert.True((await db.AccesosPlataforma.SingleAsync()).Activo);
        Assert.True((await db.RolesPlataforma.SingleAsync()).PuedeGestionarOrganizaciones);
    }

    [Fact]
    public async Task ExplicitPasswordSyncRefusesToCreateAnAbsentPlatformIdentity()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AuthenticationTestData.PlatformRepository(db)
                .SincronizarPasswordExistenteAsync("admin@nexoruta.test", AuthenticationTestData.Password));

        Assert.Empty(await db.Usuarios.ToListAsync());
        Assert.Empty(await db.AccesosPlataforma.ToListAsync());
        Assert.Empty(await db.RolesPlataforma.ToListAsync());
    }

    [Fact]
    public async Task IncompleteOrInvalidConfigurationFailsWithoutSecretsOrPartialRows()
    {
        (string? Email, string? Password)[] invalidConfigurations =
        [
            (null, AuthenticationTestData.Password),
            ("platform@example.test", null),
            ("invalid", AuthenticationTestData.Password),
            ("platform@example.test", "tiny")
        ];

        foreach (var (email, password) in invalidConfigurations)
        {
            var database = await postgres.CreateDatabaseAsync();
            await using var db = database.CreateDbContext();
            var permissionsBefore = await db.Permisos
                .AsNoTracking()
                .OrderBy(x => x.Codigo)
                .Select(x => new { x.Id, x.Codigo, x.Nombre })
                .ToArrayAsync();

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => Seeder(db, email, password).SeedPlataformaAsync());
            if (password is not null)
                Assert.DoesNotContain(password, error.ToString());
            Assert.Empty(await db.Usuarios.ToListAsync());
            Assert.Empty(await db.RolesPlataforma.ToListAsync());
            var permissionsAfter = await db.Permisos
                .AsNoTracking()
                .OrderBy(x => x.Codigo)
                .Select(x => new { x.Id, x.Codigo, x.Nombre })
                .ToArrayAsync();

            Assert.Equal(permissionsBefore, permissionsAfter);
        }
    }

    [Fact]
    public async Task DisabledByDefaultDoesNotRequireConfigurationOrWriteAnything()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        var permisosAntes = await db.Permisos
    .AsNoTracking()
    .OrderBy(x => x.Codigo)
    .Select(x => new { x.Id, x.Codigo, x.Nombre })
    .ToArrayAsync();
        await new DatosInicialesSeeder(db, new AccesoInicial("unused@example.test", "Unused", "Unused", "Unused"), new ConfigurationBuilder().Build(),
            AuthenticationTestData.UserManager(db), AuthenticationTestData.PlatformRepository(db)).SeedPlataformaAsync();
        Assert.Empty(await db.Usuarios.ToListAsync());
        var permisosDespues = await db.Permisos
            .AsNoTracking()
            .OrderBy(x => x.Codigo)
            .Select(x => new { x.Id, x.Codigo, x.Nombre })
            .ToArrayAsync();

        Assert.Equal(permisosAntes, permisosDespues);
    }

    [Theory]
    [InlineData(TipoAccesoUsuario.Operador)]
    [InlineData(TipoAccesoUsuario.Comercio)]
    public async Task OrganizationIdentityIsNeverElevatedOrChanged(TipoAccesoUsuario type)
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        await AuthenticationTestData.PlatformRepository(db).CrearAsync(type, "Existing organization", "platform-seed@example.test", AuthenticationTestData.Password, default);
        var user = await db.Usuarios.AsNoTracking().SingleAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Seeder(db).SeedPlataformaAsync());
        Assert.Empty(await db.AccesosPlataforma.ToListAsync());
        Assert.Empty(await db.RolesPlataforma.ToListAsync());
        Assert.Equal(user.PasswordHash, (await db.Usuarios.AsNoTracking().SingleAsync()).PasswordHash);
        Assert.Single(await db.AccesosUsuario.ToListAsync());
    }

    [Fact]
    public async Task ConcurrentInitializationsAreSerializedWithoutDuplicates()
    {
        var database = await postgres.CreateDatabaseAsync();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var db = database.CreateDbContext();
            await Seeder(db).SeedPlataformaAsync();
        }));

        await using var check = database.CreateDbContext();

        Assert.Single(await check.Usuarios.ToListAsync());
        Assert.Single(await check.AccesosPlataforma.ToListAsync());
        Assert.Single(await check.RolesPlataforma.ToListAsync());
        Assert.Single(await check.RolesPlataformaPermisos.ToListAsync());

        Assert.Equal(
            new[]
            {
                "devoluciones.gestionar",
                "envios.consultar",
                "envios.gestionar",
                "plataforma.organizaciones.gestionar"
            },
            await check.Permisos
                .AsNoTracking()
                .OrderBy(x => x.Codigo)
                .Select(x => x.Codigo)
                .ToArrayAsync());

        var grant = await check.RolesPlataformaPermisos.SingleAsync();

        var permisoPlataforma = await check.Permisos.SingleAsync(
            x => x.Codigo == "plataforma.organizaciones.gestionar");

        Assert.Equal(permisoPlataforma.Id, grant.PermisoId);
    }
    [Fact]
    public async Task IncompatiblePlatformStateIsNotRepaired()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        await Seeder(db).SeedPlataformaAsync();
        (await db.AccesosPlataforma.SingleAsync()).CambiarEstado(false);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Seeder(db).SeedPlataformaAsync());
        Assert.False((await db.AccesosPlataforma.AsNoTracking().SingleAsync()).Activo);
        Assert.Single(await db.Usuarios.ToListAsync());
    }

    [Fact]
    public async Task MigrationBackfillsOnlyPreviouslyEnabledPlatformRoles()
    {
        var database = await postgres.CreateDatabaseAsync("20261016035056_AddPlatformAdministration");
        await using var db = database.CreateDbContext();

        await db.GetService<IMigrator>()
            .MigrateAsync("20261016035056_AddPlatformAdministration");

        var user = new Usuario(
            "legacy-platform@example.test",
            AuthenticationTestData.Hash("legacy-platform@example.test"));

        var role = new RolPlataforma();
        var disabledRole = new RolPlataforma();
        var access = new AccesoPlataforma(user, role);

        // Insertar usando las columnas del esquema histórico.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"Usuarios\" (\"Id\", \"Email\", \"PasswordHash\") VALUES ({user.Id}, {user.Email}, {user.PasswordHash})");

        db.AddRange(role, disabledRole, access);
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"RolesPlataforma\" SET \"PuedeGestionarOrganizaciones\" = false WHERE \"Id\" = {disabledRole.Id}");

        await db.Database.MigrateAsync();

        var persistedUser = await db.Usuarios
            .AsNoTracking()
            .SingleAsync();

        var persistedAccess = await db.AccesosPlataforma
            .AsNoTracking()
            .SingleAsync();

        Assert.Equal(user.PasswordHash, persistedUser.PasswordHash);
        Assert.Equal(0, persistedUser.Version);
        Assert.Equal(access.AccesoId, persistedAccess.AccesoId);

        var grant = await db.RolesPlataformaPermisos.SingleAsync();

        Assert.Equal(role.Id, grant.RolId);
        Assert.NotEqual(disabledRole.Id, grant.RolId);

        var permisoPlataforma = Assert.Single(
            await db.Permisos
                .AsNoTracking()
                .Where(x => x.Codigo == "plataforma.organizaciones.gestionar")
                .ToListAsync());

        Assert.Equal(permisoPlataforma.Id, grant.PermisoId);

        Assert.Equal(
            new[]
            {
                "devoluciones.gestionar",
                "envios.consultar",
                "envios.gestionar",
                "plataforma.organizaciones.gestionar"
            },
            await db.Permisos
                .AsNoTracking()
                .OrderBy(x => x.Codigo)
                .Select(x => x.Codigo)
                .ToArrayAsync());
    }
    
    [Fact]
    public async Task FailureBeforeCommitRollsBackIdentityRoleAccessCatalogAndGrants()
    {
        var database = await postgres.CreateDatabaseAsync();

        await using var check = database.CreateDbContext();

        var permisosAntes = await check.Permisos
            .AsNoTracking()
            .OrderBy(x => x.Codigo)
            .Select(x => new { x.Id, x.Codigo, x.Nombre })
            .ToArrayAsync();

        await using (var db = database.CreateDbContext(new FailAfterSave()))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => Seeder(db).SeedPlataformaAsync());
        }

        Assert.Empty(await check.Usuarios.ToListAsync());
        Assert.Empty(await check.AccesosPlataforma.ToListAsync());
        Assert.Empty(await check.RolesPlataforma.ToListAsync());
        Assert.Empty(await check.RolesPlataformaPermisos.ToListAsync());

        var permisosDespues = await check.Permisos
            .AsNoTracking()
            .OrderBy(x => x.Codigo)
            .Select(x => new { x.Id, x.Codigo, x.Nombre })
            .ToArrayAsync();

        Assert.Equal(permisosAntes, permisosDespues);
    }

    private sealed class FailAfterSave : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesCompletedEventData eventData,
            int result, CancellationToken ct = default) => throw new InvalidOperationException("Injected failure before commit.");
    }
}
