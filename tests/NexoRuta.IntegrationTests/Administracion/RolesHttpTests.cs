using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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

    [Theory]
    [InlineData("role")]
    [InlineData("hash")]
    public async Task IncompleteIdentityIsBlockedForLoginJwtAndDemo(string missing)
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
}
