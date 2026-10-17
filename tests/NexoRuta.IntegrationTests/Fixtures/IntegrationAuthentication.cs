using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace NexoRuta.IntegrationTests.Fixtures;

internal static class IntegrationAuthentication
{
    public static async Task SetBearerForAccessAsync(
        this HttpClient client,
        IntegrationDatabase database,
        Guid accessId)
    {
        await using var db = database.CreateDbContext();
        var email = await (from access in db.AccesosUsuario.AsNoTracking()
                           join user in db.Usuarios.AsNoTracking() on access.UsuarioId equals user.Id
                           where access.Id == accessId
                           select user.Email).SingleAsync();
        using var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            Email = email,
            Password = AuthenticationTestData.Password
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}
