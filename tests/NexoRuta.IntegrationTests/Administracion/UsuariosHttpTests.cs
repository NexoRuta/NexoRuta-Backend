using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NexoRuta.Application.Administracion.Dtos;
using NexoRuta.Domain.Administracion;
using NexoRuta.IntegrationTests.Fixtures;

namespace NexoRuta.IntegrationTests.Administracion;

public sealed class UsuariosHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string NewUserPassword = "New-user-integration-password-2026!";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EditingEmployeeEmailRevokesOnlyOnEffectiveChangeAndPreservesAccess(bool operador)
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var owner = await db.AccesosUsuario.SingleAsync(x => operador ? x.OperadorId != null : x.ComercioId != null);
        var ownerUser = await db.Usuarios.SingleAsync(x => x.Id == owner.UsuarioId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            await LoginAsync(client, ownerUser.Email, AuthenticationTestData.Password));
        var role = await db.Roles.SingleAsync(x => operador
            ? x.OperadorId == owner.OperadorId && x.Id != owner.RolId
            : x.ComercioId == owner.ComercioId && x.Id != owner.RolId);
        var original = $"employee-{Guid.NewGuid():N}@example.test";
        using var creation = await client.PostAsJsonAsync("/api/usuarios", new { Email = original, Password = NewUserPassword, RolId = role.Id });
        Assert.Equal(HttpStatusCode.Created, creation.StatusCode);
        var employee = (await creation.Content.ReadFromJsonAsync<UsuarioOrganizacionResumen>())!;
        var passwordHash = (await ReadUserAsync(database, employee.UsuarioId)).PasswordHash;
        var oldToken = await LoginAsync(client, original, NewUserPassword);
        var changed = operador ? CreateBoundaryEmail(56).ToUpperInvariant()
            : $"CHANGED-{Guid.NewGuid():N}@EXAMPLE.TEST";

        using var update = await client.PatchAsJsonAsync($"/api/usuarios/{employee.UsuarioId}/email", new { Email = changed, Version = employee.Version });
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        var after = await ReadUserAsync(database, employee.UsuarioId);
        Assert.Equal(changed.ToLowerInvariant(), after.Email);
        Assert.Equal(1, after.Version);
        Assert.Equal(employee.UsuarioId, after.Id);
        Assert.Equal(passwordHash, after.PasswordHash);
        var access = await ReadStateAsync(database, employee.UsuarioId);
        Assert.Equal((true, 1L), access);
        await using (var accessCheck = database.CreateDbContext())
        {
            var persisted = await accessCheck.AccesosUsuario.SingleAsync(x => x.UsuarioId == employee.UsuarioId);
            Assert.Equal(role.Id, persisted.RolId);
            Assert.Equal(owner.OperadorId, persisted.OperadorId);
            Assert.Equal(owner.ComercioId, persisted.ComercioId);
            Assert.False(persisted.EsPropietario);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetWithTokenAsync(client, "/api/usuarios/actual", oldToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", new { Email = original, Password = NewUserPassword })).StatusCode);
        var newToken = await LoginAsync(client, after.Email, NewUserPassword);
        Assert.Equal(HttpStatusCode.OK, (await GetWithTokenAsync(client, "/api/usuarios/actual", newToken)).StatusCode);

        using var unchanged = await client.PatchAsJsonAsync($"/api/usuarios/{employee.UsuarioId}/email", new { Email = after.Email.ToUpperInvariant(), Version = 1 });
        Assert.Equal(HttpStatusCode.NoContent, unchanged.StatusCode);
        Assert.Equal(1, (await ReadUserAsync(database, employee.UsuarioId)).Version);
        Assert.Equal((true, 1L), await ReadStateAsync(database, employee.UsuarioId));
        Assert.Equal(HttpStatusCode.OK, (await GetWithTokenAsync(client, "/api/usuarios/actual", newToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PatchAsJsonAsync($"/api/usuarios/{employee.UsuarioId}/email", new { Email = "stale@example.test", Version = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PatchAsJsonAsync($"/api/usuarios/{owner.UsuarioId}/email", new { Email = "own@example.test", Version = 0 })).StatusCode);
    }

    [Fact]
    public async Task EmailEditRejectsForeignProtectedDuplicateAndInvalidRequests()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var owner = await AuthenticateOperatorAdminAsync(db, client);
        var foreign = await db.AccesosUsuario.SingleAsync(x => x.ComercioId != null);
        var employeeId = await CreateOperatorUserAsync(client, db, owner, $"edit-{Guid.NewGuid():N}@example.test");
        var path = $"/api/usuarios/{employeeId}/email";
        Assert.Equal(HttpStatusCode.NotFound, (await client.PatchAsJsonAsync($"/api/usuarios/{foreign.UsuarioId}/email", new { Email = "foreign@example.test", Version = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PatchAsJsonAsync(path, new { Email = (await ReadUserAsync(database, foreign.UsuarioId)).Email, Version = 0 })).StatusCode);
        foreach (var invalid in new object[] { new { Email = "invalid", Version = (long?)0 }, new { Email = "", Version = (long?)0 }, new { Email = new string('a', 255), Version = (long?)0 }, new { Email = "valid@example.test", Version = (long?)null } })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsJsonAsync(path, invalid)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsJsonAsync(path, new { Email = "valid@example.test", Version = 0, RolId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PatchAsJsonAsync($"/api/usuarios/{owner.UsuarioId}/email", new { Email = "protected@example.test", Version = 0 })).StatusCode);
        using var protectedCreation = await client.PostAsJsonAsync("/api/usuarios", new
        { Email = $"protected-{Guid.NewGuid():N}@example.test", Password = NewUserPassword, RolId = owner.RolId });
        Assert.Equal(HttpStatusCode.Created, protectedCreation.StatusCode);
        var protectedUser = (await protectedCreation.Content.ReadFromJsonAsync<UsuarioOrganizacionResumen>())!;
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PatchAsJsonAsync($"/api/usuarios/{protectedUser.UsuarioId}/email", new { Email = "blocked@example.test", Version = 0 })).StatusCode);
        Assert.Equal(0, (await ReadUserAsync(database, employeeId)).Version);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            await LoginAsync(client, (await ReadUserAsync(database, employeeId)).Email, NewUserPassword));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PatchAsJsonAsync($"/api/usuarios/{protectedUser.UsuarioId}/email",
            new { Email = "without-permission@example.test", Version = 0 })).StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OwnerAccessAndRoleAreImmutableForAnotherAdminAndTheOwner(bool operador)
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var ownerAccess = await db.AccesosUsuario.SingleAsync(x => x.EsPropietario &&
            (operador ? x.OperadorId != null : x.ComercioId != null));
        var owner = await db.Usuarios.SingleAsync(x => x.Id == ownerAccess.UsuarioId);
        var ownerRoleId = ownerAccess.RolId!.Value;
        var ownerRole = await db.Roles.SingleAsync(x => x.Id == ownerRoleId);
        var ownerPermissions = await db.RolesPermisos.Where(x => x.RolId == ownerRoleId)
            .Select(x => x.PermisoId).Order().ToArrayAsync();

        // The persisted ownership flag, not the system-role marker, protects the account and role.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Roles\" SET \"EsProtegidoSistema\" = FALSE WHERE \"Id\" = {ownerRoleId}");
        var managerEmail = $"other-admin-{Guid.NewGuid():N}@example.test";
        var manager = new Usuario(managerEmail, owner.PasswordHash!);
        var managerRole = new Rol($"owner-manager-{Guid.NewGuid():N}", "Other administrator",
            ownerAccess.OperadorId, ownerAccess.ComercioId);
        var permissions = await db.RolesPermisos.Where(x => x.RolId == ownerRoleId)
            .Select(x => x.PermisoId).ToListAsync();
        db.AddRange(manager, managerRole,
            new AccesoUsuario(manager.Id, ownerAccess.OperadorId, ownerAccess.ComercioId, managerRole));
        db.AddRange(permissions.Select(id => new RolPermiso(managerRole.Id, id)));
        await db.SaveChangesAsync();

        var ownerToken = await LoginAsync(client, owner.Email, AuthenticationTestData.Password);
        var managerToken = await LoginAsync(client, managerEmail, AuthenticationTestData.Password);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);

        using (var listing = await client.GetAsync("/api/usuarios"))
        {
            Assert.Equal(HttpStatusCode.OK, listing.StatusCode);
            var rows = await listing.Content.ReadFromJsonAsync<UsuarioOrganizacionResumen[]>();
            Assert.Contains(rows!, x => x.UsuarioId == owner.Id && x.EsPropietario);
        }
        using (var roles = await client.GetAsync("/api/roles"))
        {
            Assert.Equal(HttpStatusCode.OK, roles.StatusCode);
            var rows = await roles.Content.ReadFromJsonAsync<JsonElement[]>();
            Assert.Contains(rows!, x => x.GetProperty("id").GetGuid() == ownerRoleId &&
                x.GetProperty("esRolPropietario").GetBoolean());
        }

        async Task AssertOwnerMutationsRejectedAsync()
        {
            using var email = await client.PatchAsJsonAsync($"/api/usuarios/{owner.Id}/email",
                new { Email = $"changed-{Guid.NewGuid():N}@example.test", Version = owner.Version });
            using var state = await PatchStateAsync(client, owner.Id, false);
            using var assignRole = await client.PutAsJsonAsync($"/api/roles/accesos/{ownerAccess.Id}", new { RolId = managerRole.Id });
            using var removeRole = await client.PutAsJsonAsync($"/api/roles/accesos/{ownerAccess.Id}", new { RolId = (Guid?)null });
            using var renameRole = await client.PutAsJsonAsync($"/api/roles/{ownerRoleId}", new { Nombre = "Changed owner role" });
            using var deleteRole = await client.DeleteAsync($"/api/roles/{ownerRoleId}");
            using var changePermissions = await client.PutAsJsonAsync($"/api/roles/{ownerRoleId}/permisos",
                new { Permisos = Array.Empty<string>() });
            Assert.All(new[] { email.StatusCode, state.StatusCode, assignRole.StatusCode,
                removeRole.StatusCode, renameRole.StatusCode, deleteRole.StatusCode,
                changePermissions.StatusCode }, status => Assert.Equal(HttpStatusCode.Forbidden, status));
        }

        await AssertOwnerMutationsRejectedAsync();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
        await AssertOwnerMutationsRejectedAsync();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);
        using (var ownAccessState = await PatchStateAsync(client, manager.Id, false))
            Assert.Equal(HttpStatusCode.Forbidden, ownAccessState.StatusCode);
        Assert.Equal((true, 0L), await ReadStateAsync(database, manager.Id));

        await using (var verification = database.CreateDbContext())
        {
            var actualOwner = await verification.Usuarios.AsNoTracking().SingleAsync(x => x.Id == owner.Id);
            var actualAccess = await verification.AccesosUsuario.AsNoTracking().SingleAsync(x => x.Id == ownerAccess.Id);
            var actualRole = await verification.Roles.AsNoTracking().SingleAsync(x => x.Id == ownerRoleId);
            var actualPermissions = await verification.RolesPermisos.Where(x => x.RolId == ownerRoleId)
                .Select(x => x.PermisoId).Order().ToArrayAsync();
            Assert.Equal(owner.Email, actualOwner.Email);
            Assert.Equal(owner.Version, actualOwner.Version);
            Assert.Equal(ownerAccess.Activo, actualAccess.Activo);
            Assert.Equal(ownerAccess.RolId, actualAccess.RolId);
            Assert.Equal(ownerAccess.EsPropietario, actualAccess.EsPropietario);
            Assert.Equal(ownerAccess.VersionSesion, actualAccess.VersionSesion);
            Assert.Equal(ownerRole.Codigo, actualRole.Codigo);
            Assert.Equal(ownerRole.Nombre, actualRole.Nombre);
            Assert.Equal(ownerPermissions, actualPermissions);
        }
        Assert.Equal(HttpStatusCode.OK, (await GetWithTokenAsync(client, "/api/usuarios/actual", ownerToken)).StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", managerToken);
        var employeeEmail = $"employee-{Guid.NewGuid():N}@example.test";
        var employeeRole = await db.Roles.Where(x => x.OperadorId == ownerAccess.OperadorId &&
            x.ComercioId == ownerAccess.ComercioId && x.Id != ownerRoleId).OrderBy(x => x.Codigo).FirstAsync();
        using var created = await client.PostAsJsonAsync("/api/usuarios", new
        { Email = employeeEmail, Password = NewUserPassword, RolId = employeeRole.Id });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var employee = (await created.Content.ReadFromJsonAsync<UsuarioOrganizacionResumen>())!;
        using var emailChange = await client.PatchAsJsonAsync($"/api/usuarios/{employee.UsuarioId}/email",
            new { Email = $"changed-{Guid.NewGuid():N}@example.test", Version = employee.Version });
        using var roleChange = await client.PutAsJsonAsync($"/api/roles/accesos/{employee.AccesoId}",
            new { RolId = managerRole.Id });
        using var stateChange = await PatchStateAsync(client, employee.UsuarioId, false);
        Assert.Equal(HttpStatusCode.NoContent, emailChange.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, roleChange.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, stateChange.StatusCode);
    }

    [Fact]
    public async Task ConcurrentEmailEditsHaveOneWinnerAndNoPartialRevocation()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var owner = await AuthenticateOperatorAdminAsync(db, client);
        var first = await CreateOperatorUserAsync(client, db, owner, $"first-{Guid.NewGuid():N}@example.test");
        var second = await CreateOperatorUserAsync(client, db, owner, $"second-{Guid.NewGuid():N}@example.test");
        var shared = $"shared-{Guid.NewGuid():N}@example.test";
        var responses = await Task.WhenAll(
            client.PatchAsJsonAsync($"/api/usuarios/{first}/email", new { Email = shared, Version = 0 }),
            client.PatchAsJsonAsync($"/api/usuarios/{second}/email", new { Email = shared, Version = 0 }));
        using (responses[0]) using (responses[1])
        {
            Assert.Single(responses, x => x.StatusCode == HttpStatusCode.NoContent);
            Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        }
        await using var check = database.CreateDbContext();
        Assert.Single(await check.Usuarios.Where(x => x.Email == shared).ToListAsync());
        var accesses = await check.AccesosUsuario.Where(x => x.UsuarioId == first || x.UsuarioId == second).ToListAsync();
        Assert.Equal(1, accesses.Sum(x => x.VersionSesion));
    }

    [Fact]
    public async Task LoginAndJwtFollowUserStateAndVersionChangesOnlyOnTransitions()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var actor = await AuthenticateOperatorAdminAsync(db, client);
        var userId = await CreateOperatorUserAsync(client, db, actor, $"lifecycle-{Guid.NewGuid():N}@example.test");

        var initial = await ReadStateAsync(database, userId);
        Assert.True(initial.Activo);
        Assert.Equal(0, initial.VersionSesion);

        var oldToken = await LoginAsync(client, (await ReadUserAsync(database, userId)).Email, NewUserPassword);
        using var before = await GetWithTokenAsync(client, "/api/envios", oldToken);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        using var deactivate = await PatchStateAsync(client, userId, false);
        Assert.Equal(HttpStatusCode.NoContent, deactivate.StatusCode);
        var inactive = await ReadStateAsync(database, userId);
        Assert.False(inactive.Activo);
        Assert.Equal(1, inactive.VersionSesion);

        using var inactiveLogin = await client.PostAsJsonAsync("/api/auth/login", new
        {
            Email = (await ReadUserAsync(database, userId)).Email,
            Password = NewUserPassword
        });
        Assert.Equal(HttpStatusCode.Unauthorized, inactiveLogin.StatusCode);
        using var revoked = await GetWithTokenAsync(client, "/api/envios", oldToken);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);

        using var repeatedDeactivate = await PatchStateAsync(client, userId, false);
        Assert.Equal(HttpStatusCode.NoContent, repeatedDeactivate.StatusCode);
        Assert.Equal(1, (await ReadStateAsync(database, userId)).VersionSesion);

        using var reactivate = await PatchStateAsync(client, userId, true);
        Assert.Equal(HttpStatusCode.NoContent, reactivate.StatusCode);
        var reactivated = await ReadStateAsync(database, userId);
        Assert.True(reactivated.Activo);
        Assert.Equal(2, reactivated.VersionSesion);

        using var repeatedReactivate = await PatchStateAsync(client, userId, true);
        Assert.Equal(HttpStatusCode.NoContent, repeatedReactivate.StatusCode);
        Assert.Equal(2, (await ReadStateAsync(database, userId)).VersionSesion);

        using var stillRevoked = await GetWithTokenAsync(client, "/api/envios", oldToken);
        Assert.Equal(HttpStatusCode.Unauthorized, stillRevoked.StatusCode);
        var newToken = await LoginAsync(client, (await ReadUserAsync(database, userId)).Email, NewUserPassword);
        using var afterRelogin = await GetWithTokenAsync(client, "/api/envios", newToken);
        Assert.Equal(HttpStatusCode.OK, afterRelogin.StatusCode);
    }

    [Fact]
    public async Task ActivatingTestGmailChangesOnlyThatAccess()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var actor = await AuthenticateOperatorAdminAsync(db, client);
        var targetId = await CreateOperatorUserAsync(client, db, actor, "test@gmail.com");

        await using (var setup = database.CreateDbContext())
        {
            var target = await setup.AccesosUsuario.SingleAsync(x => x.UsuarioId == targetId);
            target.CambiarEstado(false);
            await setup.SaveChangesAsync();
        }

        async Task<Dictionary<Guid, (bool Activo, long VersionSesion)>> SnapshotAsync()
        {
            await using var snapshotDb = database.CreateDbContext();
            return await snapshotDb.AccesosUsuario.AsNoTracking()
                .Where(x => x.OperadorId == actor.OperadorId && x.ComercioId == actor.ComercioId)
                .ToDictionaryAsync(x => x.UsuarioId, x => (x.Activo, x.VersionSesion));
        }

        var before = await SnapshotAsync();
        Assert.False(before[targetId].Activo);
        using var activation = await PatchStateAsync(client, targetId, true);
        Assert.Equal(HttpStatusCode.NoContent, activation.StatusCode);
        var after = await SnapshotAsync();

        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (userId, state) in before)
            Assert.Equal(userId == targetId ? (true, state.VersionSesion + 1) : state, after[userId]);
    }

    [Fact]
    public async Task OperatorAdministratorCannotListOrChangeAnotherOrganizationsUsersOrAssignItsRole()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var actor = await AuthenticateOperatorAdminAsync(db, client);
        var foreignAccess = await db.AccesosUsuario.SingleAsync(x => x.ComercioId != null);
        var foreignUser = await db.Usuarios.SingleAsync(x => x.Id == foreignAccess.UsuarioId);

        using var listing = await client.GetAsync("/api/usuarios");
        Assert.Equal(HttpStatusCode.OK, listing.StatusCode);
        var listedUsers = await listing.Content.ReadFromJsonAsync<UsuarioOrganizacionResumen[]>();
        Assert.NotNull(listedUsers);
        Assert.Contains(listedUsers, x => x.UsuarioId == actor.UsuarioId && x.AccesoId == actor.Id && x.Activo);
        Assert.DoesNotContain(listedUsers, x => x.UsuarioId == foreignUser.Id);

        using var foreignDeactivation = await PatchStateAsync(client, foreignUser.Id, false);
        Assert.Equal(HttpStatusCode.NotFound, foreignDeactivation.StatusCode);
        var unchanged = await ReadStateAsync(database, foreignUser.Id);
        Assert.True(unchanged.Activo);
        Assert.Equal(0, unchanged.VersionSesion);

        var email = $"foreign-role-{Guid.NewGuid():N}@example.test";
        using var foreignRoleCreation = await client.PostAsJsonAsync("/api/usuarios", new
        {
            Email = email,
            Password = NewUserPassword,
            RolId = foreignAccess.RolId
        });
        Assert.Equal(HttpStatusCode.Forbidden, foreignRoleCreation.StatusCode);
        await using var verification = database.CreateDbContext();
        Assert.False(await verification.Usuarios.AnyAsync(x => x.Email == email));
    }

    [Fact]
    public async Task SelfDeactivationIsRejectedAndUsersWithoutManagementPermissionCannotManageUsers()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var actor = await AuthenticateOperatorAdminAsync(db, client);

        using var selfDeactivation = await PatchStateAsync(client, actor.UsuarioId, false);
        Assert.Equal(HttpStatusCode.Forbidden, selfDeactivation.StatusCode);
        var actorState = await ReadStateAsync(database, actor.UsuarioId);
        Assert.True(actorState.Activo);
        Assert.Equal(0, actorState.VersionSesion);

        var userId = await CreateOperatorUserAsync(client, db, actor, $"restricted-{Guid.NewGuid():N}@example.test");
        var userAccess = await db.AccesosUsuario.SingleAsync(x => x.UsuarioId == userId);
        var userToken = await LoginAsync(client, (await ReadUserAsync(database, userId)).Email, NewUserPassword);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
        using var deniedListing = await client.GetAsync("/api/usuarios");
        Assert.Equal(HttpStatusCode.Forbidden, deniedListing.StatusCode);

        var email = $"denied-create-{Guid.NewGuid():N}@example.test";
        using var deniedCreation = await client.PostAsJsonAsync("/api/usuarios", new
        {
            Email = email,
            Password = NewUserPassword,
            RolId = userAccess.RolId
        });
        Assert.Equal(HttpStatusCode.Forbidden, deniedCreation.StatusCode);
        using var deniedChange = await PatchStateAsync(client, actor.UsuarioId, false);
        Assert.Equal(HttpStatusCode.Forbidden, deniedChange.StatusCode);

        await using var verification = database.CreateDbContext();
        Assert.False(await verification.Usuarios.AnyAsync(x => x.Email == email));
        var stillActive = await ReadStateAsync(database, actor.UsuarioId);
        Assert.True(stillActive.Activo);
        Assert.Equal(0, stillActive.VersionSesion);
    }

    [Fact]
    public async Task CommerceAdministratorCanManageOnlyItsUsersRolesAndRevokesSessionsOnStateChanges()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();

        var commerceActor = await db.AccesosUsuario.SingleAsync(x => x.ComercioId != null);
        var operatorAccess = await db.AccesosUsuario.SingleAsync(x => x.OperadorId != null);
        var operatorUser = await db.Usuarios.SingleAsync(x => x.Id == operatorAccess.UsuarioId);
        var commerceId = commerceActor.ComercioId!.Value;
        var commerceRole = await db.Roles.SingleAsync(x => x.Id == commerceActor.RolId);
        var commerceMemberRole = await db.Roles.SingleAsync(x => x.ComercioId == commerceId && x.Id != commerceRole.Id);

        var otherCommerce = new Comercio("Foreign commerce");
        var otherCommerceRole = new Rol("foreign_commerce_role", "Foreign commerce role", null, otherCommerce.Id);
        var otherCommerceEmail = $"foreign-commerce-{Guid.NewGuid():N}@example.test";
        var otherCommerceUser = new Usuario(otherCommerceEmail, AuthenticationTestData.Hash(otherCommerceEmail));
        var otherCommerceAccess = new AccesoUsuario(otherCommerceUser.Id, null, otherCommerce.Id, otherCommerceRole);
        db.AddRange(otherCommerce, otherCommerceRole, otherCommerceUser, otherCommerceAccess);
        await db.SaveChangesAsync();

        await client.SetBearerForAccessAsync(database, commerceActor.Id);

        using var listing = await client.GetAsync("/api/usuarios");
        Assert.Equal(HttpStatusCode.OK, listing.StatusCode);
        var listed = await listing.Content.ReadFromJsonAsync<UsuarioOrganizacionResumen[]>();
        Assert.NotNull(listed);
        Assert.Contains(listed, x => x.AccesoId == commerceActor.Id);
        Assert.DoesNotContain(listed, x => x.AccesoId == operatorAccess.Id || x.AccesoId == otherCommerceAccess.Id);

        using var assignableResponse = await client.GetAsync("/api/roles/asignables");
        Assert.Equal(HttpStatusCode.OK, assignableResponse.StatusCode);
        var assignable = await assignableResponse.Content.ReadFromJsonAsync<RolResumen[]>();
        Assert.NotNull(assignable);
        var expectedCommerceRoles = await db.Roles.AsNoTracking()
            .Where(x => x.ComercioId == commerceId)
            .Select(x => x.Id)
            .ToArrayAsync();
        Assert.Equal(expectedCommerceRoles.Order(), assignable.Select(x => x.Id).Order());
        Assert.DoesNotContain(assignable, x => x.Id == operatorAccess.RolId || x.Id == otherCommerceRole.Id);

        using var selfDeactivation = await PatchStateAsync(client, commerceActor.UsuarioId, false);
        Assert.Equal(HttpStatusCode.Forbidden, selfDeactivation.StatusCode);
        Assert.True((await ReadStateAsync(database, commerceActor.UsuarioId)).Activo);

        var createdEmail = $"commerce-user-{Guid.NewGuid():N}@example.test";
        using var createdResponse = await client.PostAsJsonAsync("/api/usuarios", new
        {
            Email = createdEmail,
            Password = NewUserPassword,
            RolId = commerceRole.Id
        });
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = await createdResponse.Content.ReadFromJsonAsync<UsuarioOrganizacionResumen>();
        Assert.NotNull(created);
        Assert.Equal(commerceRole.Id, created.RolId);
        Assert.Equal(createdEmail, created.Email);
        await using (var scopeCheck = database.CreateDbContext())
        {
            var access = await scopeCheck.AccesosUsuario.SingleAsync(x => x.Id == created.AccesoId);
            Assert.Equal(commerceId, access.ComercioId);
            Assert.Null(access.OperadorId);
        }

        var rejectedEmails = new List<string>();
        foreach (var foreignRoleId in new[] { operatorAccess.RolId!.Value, otherCommerceRole.Id })
        {
            var rejectedEmail = $"rejected-{Guid.NewGuid():N}@example.test";
            rejectedEmails.Add(rejectedEmail);
            using var rejectedCreation = await client.PostAsJsonAsync("/api/usuarios", new
            {
                Email = rejectedEmail,
                Password = NewUserPassword,
                RolId = foreignRoleId
            });
            Assert.Equal(HttpStatusCode.Forbidden, rejectedCreation.StatusCode);
        }

        foreach (var foreignUserId in new[] { operatorUser.Id, otherCommerceUser.Id })
        {
            using var rejectedStateChange = await PatchStateAsync(client, foreignUserId, false);
            Assert.Equal(HttpStatusCode.NotFound, rejectedStateChange.StatusCode);
            Assert.True((await ReadStateAsync(database, foreignUserId)).Activo);
        }

        using var rejectedForeignRole = await client.PutAsJsonAsync(
            $"/api/roles/accesos/{created.AccesoId}", new { RolId = operatorAccess.RolId });
        Assert.Equal(HttpStatusCode.Forbidden, rejectedForeignRole.StatusCode);
        using var rejectedForeignAccess = await client.PutAsJsonAsync(
            $"/api/roles/accesos/{operatorAccess.Id}", new { RolId = commerceMemberRole.Id });
        Assert.Equal(HttpStatusCode.Forbidden, rejectedForeignAccess.StatusCode);
        await using (var rejectedCheck = database.CreateDbContext())
        {
            Assert.False(await rejectedCheck.Usuarios.AnyAsync(x => rejectedEmails.Contains(x.Email)));
            Assert.Equal(commerceRole.Id, (await rejectedCheck.AccesosUsuario.SingleAsync(x => x.Id == created.AccesoId)).RolId);
            Assert.Equal(operatorAccess.RolId, (await rejectedCheck.AccesosUsuario.SingleAsync(x => x.Id == operatorAccess.Id)).RolId);
            Assert.Equal(otherCommerceRole.Id, (await rejectedCheck.AccesosUsuario.SingleAsync(x => x.Id == otherCommerceAccess.Id)).RolId);
        }

        using var assigned = await client.PutAsJsonAsync(
            $"/api/roles/accesos/{created.AccesoId}", new { RolId = commerceMemberRole.Id });
        Assert.Equal(HttpStatusCode.NoContent, assigned.StatusCode);
        await using (var assignedCheck = database.CreateDbContext())
            Assert.Equal(commerceMemberRole.Id, (await assignedCheck.AccesosUsuario.SingleAsync(x => x.Id == created.AccesoId)).RolId);
        using var withdrawn = await client.PutAsJsonAsync(
            $"/api/roles/accesos/{created.AccesoId}", new { RolId = (Guid?)null });
        Assert.Equal(HttpStatusCode.NoContent, withdrawn.StatusCode);
        using var reassigned = await client.PutAsJsonAsync(
            $"/api/roles/accesos/{created.AccesoId}", new { RolId = commerceRole.Id });
        Assert.Equal(HttpStatusCode.NoContent, reassigned.StatusCode);

        var oldToken = await LoginAsync(client, createdEmail, NewUserPassword);
        using var beforeDisable = await GetWithTokenAsync(client, "/api/usuarios/actual", oldToken);
        Assert.Equal(HttpStatusCode.OK, beforeDisable.StatusCode);
        using var deactivated = await PatchStateAsync(client, created.UsuarioId, false);
        Assert.Equal(HttpStatusCode.NoContent, deactivated.StatusCode);
        using var inactiveLogin = await client.PostAsJsonAsync("/api/auth/login", new
        {
            Email = createdEmail,
            Password = NewUserPassword
        });
        Assert.Equal(HttpStatusCode.Unauthorized, inactiveLogin.StatusCode);
        using var oldAfterDisable = await GetWithTokenAsync(client, "/api/usuarios/actual", oldToken);
        Assert.Equal(HttpStatusCode.Unauthorized, oldAfterDisable.StatusCode);
        using var repeatedDeactivate = await PatchStateAsync(client, created.UsuarioId, false);
        Assert.Equal(HttpStatusCode.NoContent, repeatedDeactivate.StatusCode);
        Assert.Equal((false, 1L), await ReadStateAsync(database, created.UsuarioId));

        using var reactivated = await PatchStateAsync(client, created.UsuarioId, true);
        Assert.Equal(HttpStatusCode.NoContent, reactivated.StatusCode);
        using var repeatedReactivate = await PatchStateAsync(client, created.UsuarioId, true);
        Assert.Equal(HttpStatusCode.NoContent, repeatedReactivate.StatusCode);
        using var stillRevoked = await GetWithTokenAsync(client, "/api/usuarios/actual", oldToken);
        Assert.Equal(HttpStatusCode.Unauthorized, stillRevoked.StatusCode);
        var newToken = await LoginAsync(client, createdEmail, NewUserPassword);
        using var afterRelogin = await GetWithTokenAsync(client, "/api/usuarios/actual", newToken);
        Assert.Equal(HttpStatusCode.OK, afterRelogin.StatusCode);
        Assert.Equal((true, 2L), await ReadStateAsync(database, created.UsuarioId));
    }

    [Fact]
    public async Task CreatingUserAcceptsAnEmailAtThePersistenceLimitOf254Characters()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var actor = await AuthenticateOperatorAdminAsync(db, client);
        var role = await db.Roles.SingleAsync(x =>
            x.OperadorId == actor.OperadorId && x.Id != actor.RolId);
        var email = CreateBoundaryEmail(56);

        Assert.Equal(254, email.Length);
        using var response = await client.PostAsJsonAsync("/api/usuarios", new
        {
            Email = email,
            Password = NewUserPassword,
            RolId = role.Id
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<UsuarioOrganizacionResumen>();
        Assert.NotNull(created);
        Assert.Equal(email, created.Email);
        await using var verification = database.CreateDbContext();
        Assert.Equal(email, (await verification.Usuarios.SingleAsync(x => x.Id == created.UsuarioId)).Email);
    }

    [Fact]
    public async Task CreatingUserRejectsAnEmailAt255CharactersWithoutPersistingIt()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var actor = await AuthenticateOperatorAdminAsync(db, client);
        var role = await db.Roles.SingleAsync(x =>
            x.OperadorId == actor.OperadorId && x.Id != actor.RolId);
        var email = CreateBoundaryEmail(57);

        Assert.Equal(255, email.Length);
        using var response = await client.PostAsJsonAsync("/api/usuarios", new
        {
            Email = email,
            Password = NewUserPassword,
            RolId = role.Id
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Email", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        await using var verification = database.CreateDbContext();
        Assert.False(await verification.Usuarios.AnyAsync(x => x.Email == email));
    }

    [Fact]
    public async Task CreatingUserPreservesEmailPasswordAndRoleFieldValidation()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var actor = await AuthenticateOperatorAdminAsync(db, client);
        var role = await db.Roles.SingleAsync(x =>
            x.OperadorId == actor.OperadorId && x.Id != actor.RolId);
        var validEmail = $"validation-{Guid.NewGuid():N}@example.test";
        var invalidRequests = new object[]
        {
            new { Email = "not-an-email", Password = NewUserPassword, RolId = role.Id },
            new { Password = NewUserPassword, RolId = role.Id },
            new { Email = validEmail, RolId = role.Id },
            new { Email = validEmail, Password = "short", RolId = role.Id },
            new { Email = validEmail, Password = NewUserPassword, RolId = Guid.Empty }
        };

        foreach (var request in invalidRequests)
        {
            using var response = await client.PostAsJsonAsync("/api/usuarios", request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        await using var verification = database.CreateDbContext();
        Assert.False(await verification.Usuarios.AnyAsync(x => x.Email == validEmail));
    }

    [Fact]
    public async Task ConcurrentStateChangesWithTheSameInitialVersionReturnConflictAndPreserveStateVersion()
    {
        var database = await postgres.CreateDatabaseAsync();
        var barrier = new ConcurrentAccessUpdateBarrier();
        using var factory = database.CreateApiFactory(barrier);
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var actor = await AuthenticateOperatorAdminAsync(db, client);
        var userId = await CreateOperatorUserAsync(client, db, actor, $"concurrent-{Guid.NewGuid():N}@example.test");

        var changes = await Task.WhenAll(
            PatchStateAsync(client, userId, false),
            PatchStateAsync(client, userId, false));
        using (changes[0])
        using (changes[1])
        {
            Assert.Equal(2, barrier.Arrivals);
            Assert.Single(changes, response => response.StatusCode == HttpStatusCode.NoContent);
            Assert.Single(changes, response => response.StatusCode == HttpStatusCode.Conflict);
        }

        var final = await ReadStateAsync(database, userId);
        Assert.False(final.Activo);
        Assert.Equal(1, final.VersionSesion);
    }

    private static string CreateBoundaryEmail(int finalLabelLength) =>
        $"{new string('a', 64)}@{new string('b', 63)}.{new string('c', 63)}.{new string('d', finalLabelLength)}.test";

    private static async Task<AccesoUsuario> AuthenticateOperatorAdminAsync(
        NexoRuta.Infrastructure.Persistence.NexoRutaDbContext db,
        HttpClient client)
    {
        var actor = await db.AccesosUsuario.SingleAsync(x => x.OperadorId != null);
        var user = await db.Usuarios.SingleAsync(x => x.Id == actor.UsuarioId);
        var token = await LoginAsync(client, user.Email, AuthenticationTestData.Password);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return actor;
    }

    private static async Task<Guid> CreateOperatorUserAsync(
        HttpClient client,
        NexoRuta.Infrastructure.Persistence.NexoRutaDbContext db,
        AccesoUsuario actor,
        string email)
    {
        var role = await db.Roles.SingleAsync(x => x.OperadorId == actor.OperadorId && x.Id != actor.RolId);
        using var response = await client.PostAsJsonAsync("/api/usuarios", new
        {
            Email = email,
            Password = NewUserPassword,
            RolId = role.Id
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<UsuarioOrganizacionResumen>();
        Assert.NotNull(created);
        Assert.Equal(email, created.Email);
        Assert.Equal(role.Id, created.RolId);
        var access = await db.AccesosUsuario.SingleAsync(x => x.UsuarioId == created.UsuarioId);
        Assert.Equal(actor.OperadorId, access.OperadorId);
        Assert.Equal(actor.ComercioId, access.ComercioId);
        return created.UsuarioId;
    }

    private static async Task<string> LoginAsync(HttpClient client, string email, string password)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { Email = email, Password = password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    private static Task<HttpResponseMessage> PatchStateAsync(HttpClient client, Guid userId, bool activo) =>
        client.PatchAsJsonAsync($"/api/usuarios/{userId}/estado", new { Activo = activo });

    private static async Task<HttpResponseMessage> GetWithTokenAsync(HttpClient client, string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private static async Task<(bool Activo, long VersionSesion)> ReadStateAsync(IntegrationDatabase database, Guid userId)
    {
        await using var db = database.CreateDbContext();
        var access = await db.AccesosUsuario.SingleAsync(x => x.UsuarioId == userId);
        return (access.Activo, access.VersionSesion);
    }

    private static async Task<NexoRuta.Domain.Administracion.Usuario> ReadUserAsync(IntegrationDatabase database, Guid userId)
    {
        await using var db = database.CreateDbContext();
        return await db.Usuarios.SingleAsync(x => x.Id == userId);
    }

    private sealed class ConcurrentAccessUpdateBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource bothUpdatesReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;

        public int Arrivals => Volatile.Read(ref arrivals);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDATE \"AccesosUsuario\"", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref arrivals) == 2)
                    bothUpdatesReady.TrySetResult();

                await bothUpdatesReady.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }

            return result;
        }
    }
}
