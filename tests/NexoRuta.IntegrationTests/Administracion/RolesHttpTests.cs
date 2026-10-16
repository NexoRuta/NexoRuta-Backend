using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NexoRuta.Domain.Administracion;
using NexoRuta.IntegrationTests.Fixtures;

namespace NexoRuta.IntegrationTests.Administracion;

public sealed class RolesHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task AdministrativeLifecycleIsScopedAndRevokesExistingJwt()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var actor = await db.AccesosUsuario.SingleAsync(x => x.OperadorId != null);
        var other = await db.AccesosUsuario.SingleAsync(x => x.ComercioId != null);
        using var anonymous = await client.GetAsync("/api/roles");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var actorJwt = await Login(client, (await db.Usuarios.SingleAsync(x => x.Id == actor.UsuarioId)).Email);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", actorJwt);
        using var listing = await client.GetAsync("/api/roles");
        Assert.Equal(HttpStatusCode.OK, listing.StatusCode);
        var roles = await listing.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.Equal(2, roles!.Length);
        Assert.DoesNotContain(roles, x => x.GetProperty("id").GetGuid() == other.RolId);
        using var assignable = await client.GetAsync("/api/roles/asignables");
        Assert.Equal(HttpStatusCode.OK, assignable.StatusCode);
        var assignableRoles = await assignable.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.NotNull(assignableRoles);
        Assert.DoesNotContain(assignableRoles, x => x.GetProperty("id").GetGuid() == other.RolId);
        using var currentPermissions = await client.GetAsync("/api/usuarios/actual/permisos");
        Assert.Equal(HttpStatusCode.OK, currentPermissions.StatusCode);
        var permissionCodes = await currentPermissions.Content.ReadFromJsonAsync<string[]>();
        Assert.Contains("configuracion.gestionar", permissionCodes!);
        Assert.Contains("usuarios.gestionar", permissionCodes!);
        using var created = await client.PostAsJsonAsync("/api/roles", new { Codigo = "custom", Nombre = "Custom role" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(actor.OperadorId, (await db.Roles.SingleAsync(x => x.Id == id)).OperadorId);
        using var rename = await client.PutAsJsonAsync($"/api/roles/{id}", new { Nombre = "Edited role" });
        Assert.Equal(HttpStatusCode.NoContent, rename.StatusCode);
        using var unknown = await client.PutAsJsonAsync($"/api/roles/{id}/permisos", new { Permisos = new[] { "unknown.permission" } });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        using var grant = await client.PutAsJsonAsync($"/api/roles/{id}/permisos", new { Permisos = new[] { "envios.consultar" } });
        Assert.Equal(HttpStatusCode.NoContent, grant.StatusCode);
        using var foreignRename = await client.PutAsJsonAsync($"/api/roles/{other.RolId}", new { Nombre = "Attack" });
        Assert.Equal(HttpStatusCode.Forbidden, foreignRename.StatusCode);
        using var foreignGrant = await client.PutAsJsonAsync($"/api/roles/{other.RolId}/permisos", new { Permisos = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.Forbidden, foreignGrant.StatusCode);
        using var foreignAssign = await client.PutAsJsonAsync($"/api/roles/accesos/{other.Id}", new { RolId = id });
        Assert.Equal(HttpStatusCode.Forbidden, foreignAssign.StatusCode);
        using var crossRole = await client.PutAsJsonAsync($"/api/roles/accesos/{actor.Id}", new { RolId = other.RolId });
        Assert.Equal(HttpStatusCode.Forbidden, crossRole.StatusCode);
        var target = new Usuario("target@test.local", AuthenticationTestData.Hash("target@test.local"));
        var access = new AccesoUsuario(target.Id, actor.OperadorId, null);
        db.AddRange(target, access);
        await db.SaveChangesAsync();
        using var assign = await client.PutAsJsonAsync($"/api/roles/accesos/{access.Id}", new { RolId = id });
        Assert.Equal(HttpStatusCode.NoContent, assign.StatusCode);
        var targetJwt = await Login(client, target.Email);
        using var targetRequest = new HttpRequestMessage(HttpMethod.Get, "/api/envios");
        targetRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", targetJwt);
        using var before = await client.SendAsync(targetRequest);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        using var revoke = await client.PutAsJsonAsync($"/api/roles/{id}/permisos", new { Permisos = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        using var revokedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/envios");
        revokedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", targetJwt);
        using var after = await client.SendAsync(revokedRequest);
        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
        using var unassign = await client.PutAsJsonAsync($"/api/roles/accesos/{access.Id}", new { RolId = (Guid?)null });
        Assert.Equal(HttpStatusCode.NoContent, unassign.StatusCode);
        using var blockedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/envios");
        blockedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", targetJwt);
        using var blocked = await client.SendAsync(blockedRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, blocked.StatusCode);
        using var delete = await client.DeleteAsync($"/api/roles/{id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.False(await db.Roles.AnyAsync(x => x.Id == id));
        Assert.False(await db.RolesPermisos.AnyAsync(x => x.RolId == id));
    }

    [Fact]
    public async Task CommerceRoleCatalogAndGrantMutationsEnforcePermissionScope()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var commerceAccess = await db.AccesosUsuario.SingleAsync(x => x.ComercioId != null);
        var commerceUser = await db.Usuarios.SingleAsync(x => x.Id == commerceAccess.UsuarioId);
        var operatorRole = await db.Roles.SingleAsync(x => x.OperadorId != null && x.Codigo == "administrador_operador");
        var commerceRole = await db.Roles.SingleAsync(x => x.Id == commerceAccess.RolId);
        var configurationPermission = await db.Permisos.SingleAsync(x => x.Codigo == "configuracion.gestionar");
        db.Add(new RolPermiso(commerceRole.Id, configurationPermission.Id));
        await db.SaveChangesAsync();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Login(client, commerceUser.Email));
        using var listed = await client.GetAsync("/api/roles");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var roles = await listed.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.NotNull(roles);
        Assert.Contains(roles, role => role.GetProperty("id").GetGuid() == commerceRole.Id);
        Assert.DoesNotContain(roles, role => role.GetProperty("id").GetGuid() == operatorRole.Id);

        using var catalog = await client.GetAsync("/api/roles/permisos");
        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
        var codes = (await catalog.Content.ReadFromJsonAsync<JsonElement[]>())!
            .Select(permission => permission.GetProperty("codigo").GetString()).ToArray();
        Assert.Contains("envios.crear", codes);
        Assert.Contains("configuracion.gestionar", codes);
        Assert.DoesNotContain("envios.gestionar", codes);

        using var created = await client.PostAsJsonAsync("/api/roles", new { Codigo = "commerce-editable", Nombre = "Commerce editable" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var editableId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var validGrants = await client.PutAsJsonAsync($"/api/roles/{editableId}/permisos", new { Permisos = new[] { "envios.crear", "envios.consultar" } });
        Assert.Equal(HttpStatusCode.NoContent, validGrants.StatusCode);
        using var invalidGrant = await client.PutAsJsonAsync($"/api/roles/{editableId}/permisos", new { Permisos = new[] { "envios.crear", "envios.gestionar" } });
        Assert.Equal(HttpStatusCode.BadRequest, invalidGrant.StatusCode);
        Assert.Equal(new[] { "envios.consultar", "envios.crear" }, await db.RolesPermisos
            .Where(grant => grant.RolId == editableId)
            .Join(db.Permisos, grant => grant.PermisoId, permission => permission.Id, (_, permission) => permission.Codigo)
            .OrderBy(code => code).ToArrayAsync());
    }

    [Fact]
    public async Task OperatorCannotGrantCommerceOnlyPermissionToAnOperatorRole()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var operatorAccess = await db.AccesosUsuario.SingleAsync(x => x.OperadorId != null);
        var operatorUser = await db.Usuarios.SingleAsync(x => x.Id == operatorAccess.UsuarioId);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Login(client, operatorUser.Email));
        using var created = await client.PostAsJsonAsync("/api/roles", new { Codigo = "operator-editable", Nombre = "Operator editable" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var operatorRoleId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var update = await client.PutAsJsonAsync($"/api/roles/{operatorRoleId}/permisos", new { Permisos = new[] { "envios.crear" } });

        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
        Assert.Empty(await db.RolesPermisos.Where(grant => grant.RolId == operatorRoleId).ToListAsync());
    }

    [Fact]
    public async Task AssignedRoleDeletionReturnsConflictAndPreservesRoleAndGrants()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var actorAccess = await db.AccesosUsuario.SingleAsync(x => x.OperadorId != null);
        var actor = await db.Usuarios.SingleAsync(x => x.Id == actorAccess.UsuarioId);
        var role = new Rol("assigned-deletion", "Assigned deletion", actorAccess.OperadorId, null);
        var target = new Usuario("assigned-target@test.local", AuthenticationTestData.Hash("assigned-target@test.local"));
        db.AddRange(role, target, new AccesoUsuario(target.Id, actorAccess.OperadorId, null, role));
        await db.SaveChangesAsync();
        var originalName = role.Nombre;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Login(client, actor.Email));
        using var response = await client.DeleteAsync($"/api/roles/{role.Id}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await using var verify = database.CreateDbContext();
        Assert.Equal(originalName, (await verify.Roles.SingleAsync(x => x.Id == role.Id)).Nombre);
        Assert.Equal(role.Id, (await verify.AccesosUsuario.SingleAsync(x => x.UsuarioId == target.Id)).RolId);
    }

    [Fact]
    public async Task ActorCannotChangeOwnAccessOrAnyRoleAssignedToTheirIdentity()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var actorAccess = await db.AccesosUsuario.SingleAsync(x => x.OperadorId != null);
        var actorUser = await db.Usuarios.SingleAsync(x => x.Id == actorAccess.UsuarioId);
        var previousRole = await db.Roles.SingleAsync(x => x.Id == actorAccess.RolId);
        var previousGrants = await db.RolesPermisos.Where(x => x.RolId == previousRole.Id).ToListAsync();
        var ownRole = new Rol("actor-shared", "Actor shared role", actorAccess.OperadorId, null);
        db.Add(ownRole);
        db.AddRange(previousGrants.Select(x => new RolPermiso(ownRole.Id, x.PermisoId)));
        actorAccess.AsignarRol(ownRole);
        var sharedUser = new Usuario($"shared-{Guid.NewGuid():N}@test.local", AuthenticationTestData.Hash("shared@test.local"));
        var sharedAccess = new AccesoUsuario(sharedUser.Id, actorAccess.OperadorId, null, ownRole);
        db.AddRange(sharedUser, sharedAccess);
        await db.SaveChangesAsync();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Login(client, actorUser.Email));
        foreach (var desiredState in new[] { true, false })
        {
            using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/usuarios/{actorUser.Id}/estado")
            {
                Content = JsonContent.Create(new { Activo = desiredState })
            };
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        var otherRole = await db.Roles.SingleAsync(x => x.OperadorId == actorAccess.OperadorId && x.Id != ownRole.Id && x.Id != previousRole.Id);
        foreach (var roleId in new Guid?[] { ownRole.Id, otherRole.Id, null })
        {
            using var assignment = await client.PutAsJsonAsync($"/api/roles/accesos/{actorAccess.Id}", new { RolId = roleId });
            Assert.Equal(HttpStatusCode.Forbidden, assignment.StatusCode);
        }

        using var rename = await client.PutAsJsonAsync($"/api/roles/{ownRole.Id}", new { Nombre = "Changed by actor" });
        using var delete = await client.DeleteAsync($"/api/roles/{ownRole.Id}");
        using var grants = await client.PutAsJsonAsync($"/api/roles/{ownRole.Id}/permisos", new { Permisos = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.Forbidden, rename.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, grants.StatusCode);

        await using var verify = database.CreateDbContext();
        var persistedActorAccess = await verify.AccesosUsuario.SingleAsync(x => x.Id == actorAccess.Id);
        Assert.True(persistedActorAccess.Activo);
        Assert.Equal(0, persistedActorAccess.VersionSesion);
        Assert.Equal(ownRole.Id, persistedActorAccess.RolId);
        Assert.Equal("Actor shared role", (await verify.Roles.SingleAsync(x => x.Id == ownRole.Id)).Nombre);
        Assert.Equal(previousGrants.Select(x => x.PermisoId).Order().ToArray(),
            (await verify.RolesPermisos.Where(x => x.RolId == ownRole.Id).Select(x => x.PermisoId).Order().ToArrayAsync()));
    }

    [Fact]
    public async Task ProtectedOperatorAdministratorCannotBeChangedByAnotherAdminOrAssignedByUserManager()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var protectedRole = await db.Roles.SingleAsync(x => x.OperadorId != null && x.Codigo == "administrador_operador");
        var originalName = protectedRole.Nombre;
        var originalGrants = await db.RolesPermisos.Where(x => x.RolId == protectedRole.Id).Select(x => x.PermisoId).Order().ToArrayAsync();
        var operatorId = protectedRole.OperadorId!.Value;
        var permissions = await db.RolesPermisos.Where(x => x.RolId == protectedRole.Id).Select(x => x.PermisoId).ToListAsync();
        var secondAdminRole = new Rol("secondary-admin", "Secondary admin", operatorId, null);
        var userManagerRole = new Rol("user-manager", "User manager", operatorId, null);
        db.AddRange(secondAdminRole, userManagerRole);
        db.AddRange(permissions.Select(id => new RolPermiso(secondAdminRole.Id, id)));
        var userManagementPermission = await db.Permisos.SingleAsync(x => x.Codigo == "usuarios.gestionar");
        db.Add(new RolPermiso(userManagerRole.Id, userManagementPermission.Id));
        var otherAdmin = new Usuario("secondary-admin@test.local", AuthenticationTestData.Hash("secondary-admin@test.local"));
        var userManager = new Usuario("user-manager@test.local", AuthenticationTestData.Hash("user-manager@test.local"));
        db.AddRange(otherAdmin, userManager);
        db.AddRange(new AccesoUsuario(otherAdmin.Id, operatorId, null, secondAdminRole),
            new AccesoUsuario(userManager.Id, operatorId, null, userManagerRole));
        var target = new Usuario("assignment-target@test.local", AuthenticationTestData.Hash("assignment-target@test.local"));
        var targetAccess = new AccesoUsuario(target.Id, operatorId, null);
        db.AddRange(target, targetAccess);
        await db.SaveChangesAsync();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Login(client, otherAdmin.Email));
        using var rename = await client.PutAsJsonAsync($"/api/roles/{protectedRole.Id}", new { Nombre = "Changed protected role", EsProtegidoSistema = false });
        using var delete = await client.DeleteAsync($"/api/roles/{protectedRole.Id}");
        using var grants = await client.PutAsJsonAsync($"/api/roles/{protectedRole.Id}/permisos", new { Permisos = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.Forbidden, rename.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, grants.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Login(client, userManager.Email));
        using var assignable = await client.GetAsync("/api/roles/asignables");
        Assert.Equal(HttpStatusCode.OK, assignable.StatusCode);
        var assignableRoles = await assignable.Content.ReadFromJsonAsync<JsonElement[]>();
        Assert.DoesNotContain(assignableRoles!, x => x.GetProperty("id").GetGuid() == protectedRole.Id);
        using var assign = await client.PutAsJsonAsync($"/api/roles/accesos/{targetAccess.Id}", new { RolId = protectedRole.Id });
        Assert.Equal(HttpStatusCode.Forbidden, assign.StatusCode);
        using var create = await client.PostAsJsonAsync("/api/usuarios", new
        {
            Email = $"protected-role-{Guid.NewGuid():N}@example.test",
            Password = "Long-enough-password-2026!",
            RolId = protectedRole.Id
        });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);

        await using var verify = database.CreateDbContext();
        Assert.Equal(originalName, (await verify.Roles.SingleAsync(x => x.Id == protectedRole.Id)).Nombre);
        Assert.Equal(originalGrants, (await verify.RolesPermisos.Where(x => x.RolId == protectedRole.Id).Select(x => x.PermisoId).Order().ToArrayAsync()));
        Assert.Null((await verify.AccesosUsuario.SingleAsync(x => x.Id == targetAccess.Id)).RolId);
        Assert.False(await verify.Usuarios.AnyAsync(x => x.Email.StartsWith("protected-role-")));
    }

    [Fact]
    public async Task ConcurrentAccessRoleChangeCannotBypassTheAuthenticatedActorsRoleProtection()
    {
        var database = await postgres.CreateDatabaseAsync();
        var pause = new ActorAccessLockPause();
        using var factory = database.CreateApiFactory(pause);
        using var actorClient = factory.CreateHttpClient();
        using var otherAdminClient = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var actorAccess = await db.AccesosUsuario.SingleAsync(x => x.OperadorId != null);
        var actorUser = await db.Usuarios.SingleAsync(x => x.Id == actorAccess.UsuarioId);
        var systemRole = await db.Roles.SingleAsync(x => x.Id == actorAccess.RolId);
        var systemGrants = await db.RolesPermisos.Where(x => x.RolId == systemRole.Id).Select(x => x.PermisoId).ToListAsync();
        var actorRole = new Rol("race-actor-admin", "Race actor admin", actorAccess.OperadorId, null);
        var replacementRole = new Rol("race-replacement", "Replacement", actorAccess.OperadorId, null);
        var otherAdminRole = new Rol("race-other-admin", "Other admin", actorAccess.OperadorId, null);
        db.AddRange(actorRole, replacementRole, otherAdminRole);
        db.AddRange(systemGrants.Select(id => new RolPermiso(actorRole.Id, id)));
        db.AddRange(systemGrants.Select(id => new RolPermiso(otherAdminRole.Id, id)));
        actorAccess.AsignarRol(actorRole);
        var otherAdmin = new Usuario("race-admin@test.local", AuthenticationTestData.Hash("race-admin@test.local"));
        db.AddRange(otherAdmin, new AccesoUsuario(otherAdmin.Id, actorAccess.OperadorId, null, otherAdminRole));
        await db.SaveChangesAsync();

        actorClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Login(actorClient, actorUser.Email));
        otherAdminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Login(otherAdminClient, otherAdmin.Email));
        var mutation = actorClient.PutAsJsonAsync($"/api/roles/{actorRole.Id}", new { Nombre = "Must remain unchanged" });
        try
        {
            await pause.ActorAccessLockRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var reassignment = await otherAdminClient.PutAsJsonAsync($"/api/roles/accesos/{actorAccess.Id}", new { RolId = replacementRole.Id });
            Assert.Equal(HttpStatusCode.NoContent, reassignment.StatusCode);
        }
        finally
        {
            pause.Release.TrySetResult(true);
        }

        using var mutationResponse = await mutation;
        Assert.Equal(HttpStatusCode.Forbidden, mutationResponse.StatusCode);
        await using var verify = database.CreateDbContext();
        Assert.Equal("Race actor admin", (await verify.Roles.SingleAsync(x => x.Id == actorRole.Id)).Nombre);
        Assert.Equal(replacementRole.Id, (await verify.AccesosUsuario.SingleAsync(x => x.Id == actorAccess.Id)).RolId);
    }

    [Theory]
    [InlineData("role")]
    [InlineData("hash")]
    public async Task IncompleteIdentityIsBlockedForLoginJwtAndLegacyHeader(string missing)
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = new IntegrationApiFactory(database, "Development");
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var access = await db.AccesosUsuario.SingleAsync(x => x.ComercioId != null);
        var user = await db.Usuarios.SingleAsync(x => x.Id == access.UsuarioId);
        var jwt = await Login(client, user.Email);
        if (missing == "role") access.RetirarRol();
        else await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Usuarios\" SET \"PasswordHash\" = NULL WHERE \"Id\" = {user.Id}");
        await db.SaveChangesAsync();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        using var jwtResponse = await client.GetAsync("/api/envios");
        Assert.Equal(HttpStatusCode.Unauthorized, jwtResponse.StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        client.DefaultRequestHeaders.Add("X-NexoRuta-Acceso", access.Id.ToString());
        using var demoResponse = await client.GetAsync("/api/envios");
        Assert.Equal(HttpStatusCode.Unauthorized, demoResponse.StatusCode);
        using var login = await client.PostAsJsonAsync("/api/auth/login", new { Email = user.Email, Password = AuthenticationTestData.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    private static async Task<string> Login(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { Email = email, Password = AuthenticationTestData.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    private sealed class ActorAccessLockPause : DbCommandInterceptor
    {
        public TaskCompletionSource<bool> ActorAccessLockRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("WHERE \"UsuarioId\"", StringComparison.Ordinal))
            {
                ActorAccessLockRequested.TrySetResult(true);
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
