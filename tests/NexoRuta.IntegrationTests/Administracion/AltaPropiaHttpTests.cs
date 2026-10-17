using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using NexoRuta.Api.Core.Security;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Administracion.Repositories;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.IntegrationTests.Fixtures;

namespace NexoRuta.IntegrationTests.Administracion;

public sealed class AltaPropiaHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Password = AuthenticationTestData.Password;
    private static IntegrationApiFactory Factory(IntegrationDatabase db, params IInterceptor[] interceptors)
        => new(db, settings: new Dictionary<string, string?> { ["DatabaseInitialization:SeedData"] = "false" }, interceptors: interceptors);

    [Fact]
    public async Task RegistrationCreatesOnlyIdentityAndConcurrentDuplicatesAreControlled()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = Factory(database, new RegistrationRendezvous());
        using var client = factory.CreateHttpClient();
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            client.PostAsJsonAsync("/api/auth/register", new { Email = "  Person@EXAMPLE.test  ", Password })));
        Assert.Equal(new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }, responses.Select(x => x.StatusCode).Order());
        foreach (var response in responses)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(Password, text);
            Assert.DoesNotContain("hash", text, StringComparison.OrdinalIgnoreCase);
        }
        await using var db = database.CreateDbContext();
        Assert.Equal("person@example.test", (await db.Usuarios.SingleAsync()).Email);
        Assert.Empty(await db.AccesosUsuario.ToListAsync());
        Assert.Empty(await db.Roles.ToListAsync());
        Assert.Empty(await db.AccesosPlataforma.ToListAsync());
    }

    [Fact]
    public async Task RegistrationRejectsInvalidCredentialsAndPrivilegeFields()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = Factory(database);
        using var client = factory.CreateHttpClient();
        foreach (var body in new object[] {
            new { Email = "invalid", Password }, new { Email = "valid@example.test", Password = "short" },
            new { Email = "evil@example.test", Password, RolId = Guid.NewGuid() },
            new { Email = "evil@example.test", Password, UsuarioId = Guid.NewGuid(), TipoAcceso = "Plataforma", Permisos = new[] { "usuarios.gestionar" } } })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/register", body)).StatusCode);
        await using var db = database.CreateDbContext();
        Assert.Empty(await db.Usuarios.ToListAsync());
    }

    [Theory]
    [InlineData("comercios", TipoAccesoUsuario.Comercio)]
    [InlineData("operadores", TipoAccesoUsuario.Operador)]
    public async Task IdentityOnlySessionCreatesOwnProtectedOrganizationAndRequiresNewLogin(string kind, TipoAccesoUsuario type)
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = Factory(database);
        using var client = factory.CreateHttpClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync($"/api/alta-propia/{kind}", new { Nombre = "Own" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", new { Email = "owner@example.test", Password })).StatusCode);
        await Login(client, "owner@example.test");
        var oldToken = client.DefaultRequestHeaders.Authorization!.Parameter;
        var identity = await client.GetFromJsonAsync<JsonElement>("/api/usuarios/actual");
        Assert.Equal("SinOrganizacion", identity.GetProperty("tipo").GetString());
        foreach (var key in new[] { "accesoId", "rolId", "rolCodigo", "operadorId", "comercioId", "tipoAcceso" })
            Assert.Equal(JsonValueKind.Null, identity.GetProperty(key).ValueKind);
        var userId = identity.GetProperty("usuarioId").GetGuid();
        Assert.Empty((await client.GetFromJsonAsync<string[]>("/api/usuarios/actual/permisos"))!);
        foreach (var path in new[] { "/api/envios", "/api/usuarios", "/api/roles", "/api/admin/comercios", "/api/admin/operadores", "/api/comercio/operadores", "/api/operadores" })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/alta-propia/{kind}", new { Nombre = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/alta-propia/{kind}", new { Nombre = "Spoof", UsuarioId = Guid.NewGuid(), Password, RolId = Guid.NewGuid(), Permisos = new[] { "plataforma.organizaciones.gestionar" } })).StatusCode);
        using var created = await client.PostAsJsonAsync($"/api/alta-propia/{kind}", new { Nombre = " Own business " });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var organization = (await created.Content.ReadFromJsonAsync<AltaOrganizacion>())!;
        Assert.Equal(userId, organization.UsuarioId);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/usuarios/actual")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/envios")).StatusCode);
        await Login(client, "owner@example.test");
        var current = await client.GetFromJsonAsync<JsonElement>("/api/usuarios/actual");
        Assert.Equal(userId, current.GetProperty("usuarioId").GetGuid());
        Assert.Equal(type.ToString(), current.GetProperty("tipo").GetString());
        foreach (var other in new[] { "comercios", "operadores" })
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/alta-propia/{other}", new { Nombre = "Second" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/roles/{organization.RolId}/permisos", new { Permisos = Array.Empty<string>() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/roles/accesos/{organization.AccesoId}", new { RolId = (Guid?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PatchAsJsonAsync($"/api/usuarios/{userId}/estado", new { Activo = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/usuarios", new { Email = "owner@example.test", Password, RolId = organization.RolId })).StatusCode);
        await using var db = database.CreateDbContext();
        Assert.Equal(1, await db.Usuarios.CountAsync());
        var role = await db.Roles.SingleAsync();
        Assert.True(role.EsProtegidoSistema);
        var permissions = await (from grant in db.RolesPermisos join permission in db.Permisos on grant.PermisoId equals permission.Id select permission.Codigo).ToListAsync();
        Assert.Equal(CatalogoPermisos.Para(type).Select(x => x.Codigo).Order(), permissions.Order());
        Assert.True((await db.AccesosUsuario.SingleAsync()).Activo);
        Assert.True((await db.AccesosUsuario.SingleAsync()).EsPropietario);
        var createdAccess = await db.AccesosUsuario.SingleAsync();
        var ownership = new EfAccesosUsuarioRepository(db);
        Assert.True(await ownership.EsPropietarioAsync(userId, createdAccess.Id));
        Assert.False(await ownership.EsPropietarioAsync(Guid.NewGuid(), createdAccess.Id));
        Assert.Empty(await db.AccesosPlataforma.ToListAsync());
        Assert.Equal(type == TipoAccesoUsuario.Comercio ? 1 : 0, await db.Comercios.CountAsync());
        Assert.Equal(type == TipoAccesoUsuario.Operador ? 1 : 0, await db.Operadores.CountAsync());
        client.DefaultRequestHeaders.Authorization = new("Bearer", oldToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/usuarios/actual")).StatusCode);
    }

    [Theory]
    [InlineData("comercios", "comercios")]
    [InlineData("operadores", "operadores")]
    [InlineData("comercios", "operadores")]
    public async Task ConcurrentMembershipIsUniqueAndRollbackLeavesNoOrphans(string first, string second)
    {
        var database = await postgres.CreateDatabaseAsync();
        // Install the catalog before the rendezvous so catalog initialization cannot serialize the race.
        await using (var db = database.CreateDbContext())
        {
            db.AddRange(CatalogoPermisos.Todos.Select(p => new Permiso(p.Codigo, p.Nombre)));
            await db.SaveChangesAsync();
        }
        using var factory = Factory(database, new MembershipRendezvous());
        using var client = factory.CreateHttpClient();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", new { Email = "race@example.test", Password })).StatusCode);
        await Login(client, "race@example.test");
        var results = await Task.WhenAll(new[] { first, second }.Select(kind => client.PostAsJsonAsync($"/api/alta-propia/{kind}", new { Nombre = "Race" })));
        Assert.Equal(new[] { HttpStatusCode.Created, HttpStatusCode.Conflict }, results.Select(x => x.StatusCode).Order());
        await using var final = database.CreateDbContext();
        Assert.Equal(1, await final.Comercios.CountAsync() + await final.Operadores.CountAsync());
        Assert.Equal(1, await final.Usuarios.CountAsync());
        Assert.Equal(1, await final.AccesosUsuario.CountAsync());
        Assert.Equal(1, await final.Roles.CountAsync());
        var access = await final.AccesosUsuario.SingleAsync();
        Assert.True(access.EsPropietario);
        Assert.Equal(access.RolId, (await final.Roles.SingleAsync()).Id);
        Assert.Equal(CatalogoPermisos.Para(access.TipoOrganizacion).Count, await final.RolesPermisos.CountAsync());
    }

    [Fact]
    public async Task PlatformCannotSelfProvisionAndInactiveAccountsCannotLoginAsUnassigned()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = Factory(database);
        using var client = factory.CreateHttpClient();
        await using (var db = database.CreateDbContext())
        {
            await AuthenticationTestData.PlatformRepository(db).InicializarAsync("platform@example.test", Password);
            await AuthenticationTestData.PlatformRepository(db).CrearAsync(TipoAccesoUsuario.Comercio, "Existing", "inactive@example.test", Password, default);
        }
        await Login(client, "platform@example.test");
        foreach (var kind in new[] { "comercios", "operadores" })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/alta-propia/{kind}", new { Nombre = "Denied" })).StatusCode);
        await using (var db = database.CreateDbContext())
        {
            (await db.AccesosUsuario.SingleAsync()).CambiarEstado(false);
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", new { Email = "inactive@example.test", Password })).StatusCode);
        await using var final = database.CreateDbContext();
        Assert.Equal(1, await final.Comercios.CountAsync());
        Assert.Empty(await final.Operadores.ToListAsync());
        Assert.Equal(2, await final.Usuarios.CountAsync());
    }

    [Fact]
    public async Task TwoOwnersRemainIsolatedAndEmployeeCreationNeverAdoptsAnExistingIdentity()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = Factory(database);
        using var first = factory.CreateHttpClient();
        using var second = factory.CreateHttpClient();
        Assert.Equal(HttpStatusCode.Created, (await first.PostAsJsonAsync("/api/auth/register", new { Email = "first@example.test", Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await second.PostAsJsonAsync("/api/auth/register", new { Email = "second@example.test", Password })).StatusCode);
        await Login(first, "first@example.test");
        await Login(second, "second@example.test");
        var firstOrg = (await (await first.PostAsJsonAsync("/api/alta-propia/comercios", new { Nombre = "First" })).Content.ReadFromJsonAsync<AltaOrganizacion>())!;
        await Login(first, "first@example.test");
        // Existing unassigned identities must not be silently attached or have their password replaced.
        Assert.Equal(HttpStatusCode.Conflict, (await first.PostAsJsonAsync("/api/usuarios", new { Email = "second@example.test", Password = "Different-password-2026", RolId = firstOrg.RolId })).StatusCode);
        var secondOrg = (await (await second.PostAsJsonAsync("/api/alta-propia/comercios", new { Nombre = "Second" })).Content.ReadFromJsonAsync<AltaOrganizacion>())!;
        await Login(second, "second@example.test");
        var firstUsers = await first.GetFromJsonAsync<JsonElement>("/api/usuarios");
        var secondUsers = await second.GetFromJsonAsync<JsonElement>("/api/usuarios");
        Assert.Equal("first@example.test", firstUsers.EnumerateArray().Single().GetProperty("email").GetString());
        Assert.Equal("second@example.test", secondUsers.EnumerateArray().Single().GetProperty("email").GetString());
        Assert.DoesNotContain(secondOrg.RolId.ToString(), await (await first.GetAsync("/api/roles")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await first.PatchAsJsonAsync($"/api/usuarios/{secondOrg.UsuarioId}/estado", new { Activo = false })).StatusCode);
        await using var db = database.CreateDbContext();
        Assert.Equal(2, await db.Usuarios.CountAsync());
        Assert.Equal(2, await db.AccesosUsuario.CountAsync());
        Assert.All(await db.AccesosUsuario.ToListAsync(), x => Assert.True(x.Activo));
        Assert.All(await db.AccesosUsuario.ToListAsync(), x => Assert.True(x.EsPropietario));
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task FailureAfterSaveRollsBackEveryOrganizationRowWithoutChangingIdentity()
    {
        var database = await postgres.CreateDatabaseAsync();
        Guid userId;
        string hash;
        await using (var db = database.CreateDbContext())
        {
            var user = new Usuario("rollback@example.test", AuthenticationTestData.Hash("rollback@example.test"));
            db.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id; hash = user.PasswordHash!;
        }
        await using (var db = database.CreateDbContext(new FailAfterSave()))
            await Assert.ThrowsAsync<InvalidOperationException>(() => AuthenticationTestData.PlatformRepository(db)
                .CrearPropiaAsync(userId, TipoAccesoUsuario.Operador, "Rollback", default));
        await using var final = database.CreateDbContext();
        Assert.Equal(hash, (await final.Usuarios.SingleAsync(x => x.Id == userId)).PasswordHash);
        Assert.Empty(await final.AccesosUsuario.ToListAsync());
        Assert.Empty(await final.Operadores.ToListAsync());
        Assert.Empty(await final.Comercios.ToListAsync());
        Assert.Empty(await final.Roles.ToListAsync());
        Assert.Empty(await final.RolesPermisos.ToListAsync());
        Assert.Empty(await final.Permisos.ToListAsync());
    }

    private sealed class FailAfterSave : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
            => throw new InvalidOperationException("Injected failure before commit.");
    }

    [Theory]
    [InlineData("with-access")]
    [InlineData("wrong-version")]
    [InlineData("unknown-scope")]
    [InlineData("missing-identity")]
    public async Task IdentityOnlyJwtRejectsInconsistentBinding(string scenario)
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = Factory(database);
        using var client = factory.CreateHttpClient();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", new { Email = "token@example.test", Password })).StatusCode);
        await using var db = database.CreateDbContext();
        var user = await db.Usuarios.SingleAsync();
        var claims = new List<Claim> {
            new(JwtRegisteredClaimNames.Sub, (scenario == "missing-identity" ? Guid.NewGuid() : user.Id).ToString()),
            new(AccesoSeleccionado.ClaimAmbito, scenario == "unknown-scope" ? "unknown" : "identidad"),
            new(JwtSesionClaims.VersionSesion, scenario == "wrong-version" ? "1" : "0")
        };
        if (scenario == "with-access") claims.Add(new(AccesoSeleccionado.ClaimAccesoId, Guid.NewGuid().ToString()));
        var token = new JwtSecurityToken(AuthenticationTestData.Issuer, AuthenticationTestData.Audience, claims,
            expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Convert.FromBase64String(AuthenticationTestData.SigningKey)), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/usuarios/actual")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/alta-propia/comercios", new { Nombre = "Denied" })).StatusCode);
        Assert.Empty(await db.AccesosUsuario.ToListAsync());
        Assert.Empty(await db.Comercios.ToListAsync());
    }

    [Fact]
    public async Task SelfServiceCommerceOwnerManagesScopedRolesAndEmployeesWithoutEditingOwnPrivileges()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = Factory(database);
        using var client = factory.CreateHttpClient();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", new { Email = "roles-owner@example.test", Password })).StatusCode);
        await Login(client, "roles-owner@example.test");
        using var creation = await client.PostAsJsonAsync("/api/alta-propia/comercios", new { Nombre = "Roles commerce" });
        Assert.Equal(HttpStatusCode.Created, creation.StatusCode);
        var owner = (await creation.Content.ReadFromJsonAsync<AltaOrganizacion>())!;
        await Login(client, "roles-owner@example.test");
        var expected = new[] { "configuracion.gestionar", "envios.consultar", "envios.crear", "usuarios.gestionar" };
        Assert.Equal(expected, await client.GetFromJsonAsync<string[]>("/api/usuarios/actual/permisos"));
        var catalog = await client.GetFromJsonAsync<JsonElement>("/api/roles/permisos");
        Assert.Equal(expected, catalog.EnumerateArray().Select(x => x.GetProperty("codigo").GetString()).Order());
        using var created = await client.PostAsJsonAsync("/api/roles", new { Codigo = "equipo", Nombre = "Team" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var roleId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var grants = new[] { "envios.crear", "envios.consultar" };
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/roles/{roleId}/permisos", new { Permisos = grants })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/roles/{roleId}", new { Nombre = "Editable team" })).StatusCode);
        foreach (var denied in new[] { "envios.gestionar", RolPlataforma.GestionarOrganizaciones })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/roles/{roleId}/permisos", new { Permisos = new[] { denied } })).StatusCode);
        var roles = await client.GetFromJsonAsync<JsonElement>("/api/roles");
        var team = roles.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == roleId);
        Assert.Equal("Editable team", team.GetProperty("nombre").GetString());
        Assert.Equal(grants.Order(), team.GetProperty("permisos").EnumerateArray().Select(x => x.GetString()).Order());
        var ownRole = roles.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == owner.RolId);
        Assert.True(ownRole.GetProperty("esPropio").GetBoolean());
        Assert.True(ownRole.GetProperty("esProtegidoSistema").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/roles/{owner.RolId}", new { Nombre = "Not allowed" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/roles/{owner.RolId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/roles/{owner.RolId}/permisos", new { Permisos = grants })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/roles/accesos/{owner.AccesoId}", new { RolId = roleId })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PatchAsJsonAsync($"/api/usuarios/{owner.UsuarioId}/estado", new { Activo = false })).StatusCode);
        using var employee = await client.PostAsJsonAsync("/api/usuarios", new { Email = "employee-roles@example.test", Password, RolId = roleId });
        Assert.Equal(HttpStatusCode.Created, employee.StatusCode);
        var accessId = (await employee.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accesoId").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/roles/{roleId}")).StatusCode);
        using var replacement = await client.PostAsJsonAsync("/api/roles", new { Codigo = "consulta", Nombre = "Read only" });
        Assert.Equal(HttpStatusCode.Created, replacement.StatusCode);
        var replacementId = (await replacement.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/roles/{replacementId}/permisos", new { Permisos = new[] { "envios.consultar" } })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/api/roles/accesos/{accessId}", new { RolId = replacementId })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/roles/{roleId}")).StatusCode);
        await using (var db = database.CreateDbContext())
        {
            foreach (var type in new[] { TipoAccesoUsuario.Comercio, TipoAccesoUsuario.Operador })
            {
                var foreign = await AuthenticationTestData.PlatformRepository(db).CrearAsync(type, "Other scope", $"other-{type}@example.test", Password, default);
                Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/roles/{foreign.RolId}/permisos", new { Permisos = grants })).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/roles/accesos/{foreign.AccesoId}", new { RolId = replacementId })).StatusCode);
                Assert.DoesNotContain(foreign.RolId.ToString(), await client.GetStringAsync("/api/roles"));
            }
        }
        Assert.Equal(expected, await client.GetFromJsonAsync<string[]>("/api/usuarios/actual/permisos"));
        await Login(client, "employee-roles@example.test");
        Assert.Equal(new[] { "envios.consultar" }, await client.GetFromJsonAsync<string[]>("/api/usuarios/actual/permisos"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/roles")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/usuarios")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/comercios")).StatusCode);
    }

    private static async Task Login(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { Email = email, Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());
    }

    private sealed class RegistrationRendezvous : SaveChangesInterceptor
    {
        private int count;
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<Usuario>().Any(x => x.State == EntityState.Added))
            {
                if (Interlocked.Increment(ref count) == 2) ready.TrySetResult();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(25), ct);
            }
            return result;
        }
    }

    private sealed class MembershipRendezvous : SaveChangesInterceptor
    {
        private int count;
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<AccesoUsuario>().Any(x => x.State == EntityState.Added))
            {
                if (Interlocked.Increment(ref count) == 2) ready.TrySetResult();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(25), ct);
            }
            return result;
        }
    }
}
