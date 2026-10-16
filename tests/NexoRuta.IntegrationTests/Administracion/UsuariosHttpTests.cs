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
