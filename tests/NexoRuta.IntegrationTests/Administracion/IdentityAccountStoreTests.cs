using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NexoRuta.Domain.Administracion;
using NexoRuta.IntegrationTests.Fixtures;

namespace NexoRuta.IntegrationTests.Administracion;

public sealed class IdentityAccountStoreTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task UpgradePreservesLegacyGuidHashVersionAndPlatformAccess()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        await db.GetService<IMigrator>().MigrateAsync("20261017035519_AddUsuarioEmailConcurrency");
        var previousAccountForeignKeys = await db.Database.SqlQueryRaw<string>("""
            SELECT concat(conrelid::regclass::text, ':', conname) AS "Value"
            FROM pg_constraint
            WHERE contype = 'f'
              AND confrelid = '"Usuarios"'::regclass
              AND NOT EXISTS (SELECT 1 FROM pg_class c WHERE c.oid = conrelid AND c.relname LIKE 'AspNetUser%')
            ORDER BY 1
            """).ToListAsync();
        const string email = "legacy-platform@example.test";
        const string ownerEmail = "legacy-owner@example.test";
        const string password = "Existing-password-for-identity-2026!";
        var userId = Guid.Parse("019a7b59-5e91-7d3b-8d2b-4100f99b24a8");
        var ownerId = Guid.Parse("019a7b59-5e91-7d3b-8d2b-4100f99b24a9");
        var hash = new PasswordHasher<string>().HashPassword(email, password);
        var ownerHash = new PasswordHasher<string>().HashPassword(ownerEmail, password);
        var user = new Usuario(email, hash);
        user.Id = userId;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Usuarios" ("Id", "Email", "PasswordHash", "Version")
            VALUES ({userId}, {email}, {hash}, 7)
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Usuarios" ("Id", "Email", "PasswordHash", "Version")
            VALUES ({ownerId}, {ownerEmail}, {ownerHash}, 4)
            """);
        var role = new RolPlataforma();
        var access = new AccesoPlataforma(user, role);
        access.CambiarEstado(false);
        access.CambiarEstado(true);
        var operatorEntity = new Operador("Legacy operator");
        var ownerRole = new Rol("legacy-owner", "Legacy owner", operatorEntity.Id, null);
        ownerRole.ProtegerComoRolSistema();
        var ownerAccess = new AccesoUsuario(ownerId, operatorEntity.Id, null, ownerRole, esPropietario: true);
        ownerAccess.RevocarSesiones();
        db.AddRange(role, operatorEntity, ownerRole);
        await db.SaveChangesAsync();
        db.AddRange(access, ownerAccess);
        await db.SaveChangesAsync();

        await db.Database.MigrateAsync();
        db.ChangeTracker.Clear();

        var migrated = await db.Usuarios.SingleAsync(x => x.Id == userId);
        Assert.Equal(2, await db.Usuarios.CountAsync());
        var identityUsers = AuthenticationTestData.UserManager(db);
        var found = await identityUsers.FindByEmailAsync(email);
        var foundOwner = await identityUsers.FindByEmailAsync(ownerEmail);
        Assert.NotNull(found);
        Assert.NotNull(foundOwner);
        Assert.Equal(userId, migrated.Id);
        Assert.Equal(hash, migrated.PasswordHash);
        Assert.Equal(7, migrated.Version);
        Assert.Equal(0, migrated.VersionCredencial);
        Assert.Equal(email, migrated.Email);
        Assert.Equal(email.ToUpperInvariant(), migrated.NormalizedEmail);
        Assert.True(await identityUsers.CheckPasswordAsync(found, password));
        Assert.Equal(ownerId, foundOwner.Id);
        Assert.Equal(ownerHash, foundOwner.PasswordHash);
        Assert.Equal(4, foundOwner.Version);
        Assert.Equal(0, foundOwner.VersionCredencial);
        Assert.True(await identityUsers.CheckPasswordAsync(foundOwner, password));
        Assert.Equal(access.AccesoId, (await db.AccesosPlataforma.SingleAsync()).AccesoId);
        Assert.Equal(userId, (await db.AccesosPlataforma.SingleAsync()).UsuarioId);
        Assert.Equal(2, (await db.AccesosPlataforma.SingleAsync()).VersionSesion);
        var persistedOwnerAccess = await db.AccesosUsuario.SingleAsync(x => x.UsuarioId == ownerId);
        Assert.Equal(ownerAccess.Id, persistedOwnerAccess.Id);
        Assert.True(persistedOwnerAccess.EsPropietario);
        Assert.Equal(1, persistedOwnerAccess.VersionSesion);
        Assert.Equal(operatorEntity.Id, persistedOwnerAccess.OperadorId);
        Assert.Null(await db.Database.SqlQueryRaw<string?>(
            "SELECT to_regclass('public.\"AspNetUsers\"')::text AS \"Value\"").SingleAsync());
        var currentAccountForeignKeys = await db.Database.SqlQueryRaw<string>("""
            SELECT concat(conrelid::regclass::text, ':', conname) AS "Value"
            FROM pg_constraint
            WHERE contype = 'f'
              AND confrelid = '"Usuarios"'::regclass
              AND NOT EXISTS (SELECT 1 FROM pg_class c WHERE c.oid = conrelid AND c.relname LIKE 'AspNetUser%')
            ORDER BY 1
            """).ToListAsync();
        Assert.All(previousAccountForeignKeys, key => Assert.Contains(key, currentAccountForeignKeys));
        Assert.Contains("\"SesionesMoviles\":FK_SesionesMoviles_Usuarios_UsuarioId", currentAccountForeignKeys);
    }

    [Fact]
    public async Task IdentityStoreAuthenticatesAnExistingPasswordHashWithoutCreatingASecondAccount()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<NexoRuta.Infrastructure.Persistence.NexoRutaDbContext>();
        var users = services.GetRequiredService<UserManager<Usuario>>();
        const string email = "identity-migration@example.test";
        const string password = "Existing-password-for-identity-2026!";
        var hash = new PasswordHasher<string>().HashPassword(email, password);
        var existing = new Usuario(email, hash);
        db.Usuarios.Add(existing);
        await db.SaveChangesAsync();

        var found = await users.FindByIdAsync(existing.Id.ToString());

        Assert.NotNull(found);
        Assert.True(await users.CheckPasswordAsync(found, password));
        Assert.Equal(hash, found.PasswordHash);
        Assert.Equal("Usuarios", db.Model.FindEntityType(typeof(Usuario))!.GetTableName());
        Assert.DoesNotContain(db.Model.GetEntityTypes(), x => x.ClrType == typeof(IdentityRole<Guid>));
        Assert.Equal(1, await db.Usuarios.CountAsync(x => x.Id == existing.Id));
        Assert.Null(await db.Database.SqlQueryRaw<string?>(
            "SELECT to_regclass('public.\"AspNetUsers\"')::text AS \"Value\"").SingleAsync());
        Assert.Null(await db.Database.SqlQueryRaw<string?>(
            "SELECT to_regclass('public.\"AspNetRoles\"')::text AS \"Value\"").SingleAsync());
    }
}
