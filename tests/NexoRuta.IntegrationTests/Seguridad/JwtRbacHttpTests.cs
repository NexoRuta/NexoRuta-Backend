using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NexoRuta.Api.Core.Security;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Envios;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguimiento;

namespace NexoRuta.IntegrationTests.Seguridad;

public sealed class JwtRbacHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData("valid", HttpStatusCode.OK)]
    [InlineData("wrong-password", HttpStatusCode.Unauthorized)]
    [InlineData("missing-user", HttpStatusCode.Unauthorized)]
    public async Task LoginUsesIdentityHashAndDoesNotRevealMissingUsers(string scenario, HttpStatusCode expected)
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var access = await db.AccesosUsuario.SingleAsync(x => x.ComercioId != null);
        var user = await db.Usuarios.SingleAsync(x => x.Id == access.UsuarioId);
        Assert.NotEqual(AuthenticationTestData.Password, user.PasswordHash);
        Assert.Equal(Microsoft.AspNetCore.Identity.PasswordVerificationResult.Success,
            new Microsoft.AspNetCore.Identity.PasswordHasher<string>()
                .VerifyHashedPassword(user.Email, user.PasswordHash!, AuthenticationTestData.Password));
        using var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            Email = scenario == "missing-user" ? "missing@example.test" : user.Email,
            Password = scenario == "wrong-password" ? "wrong-password" : AuthenticationTestData.Password
        });
        Assert.Equal(expected, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (scenario == "valid")
        {
            Assert.Equal("Bearer", body.GetProperty("tokenType").GetString());
            Assert.True(body.GetProperty("expiresAtUtc").GetDateTime() > DateTime.UtcNow);
            var token = body.GetProperty("accessToken").GetString()!;
            var parsed = new JwtSecurityTokenHandler().ReadJwtToken(token);
            Assert.Equal(user.Id.ToString(), parsed.Subject);
            Assert.Equal(access.Id.ToString(), parsed.Claims.Single(x => x.Type == AccesoSeleccionado.ClaimAccesoId).Value);
            Assert.DoesNotContain(parsed.Claims, x => x.Type.Contains("permiso", StringComparison.OrdinalIgnoreCase));
            using var authorized = await client.SendAsync(Request(token));
            Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
        }
        else
        {
            Assert.Equal("Credenciales inválidas.", body.GetProperty("message").GetString());
            Assert.False(body.TryGetProperty("accessToken", out _));
        }
    }

    [Theory]
    [InlineData("", "password")]
    [InlineData(" ", "password")]
    [InlineData(null, "password")]
    [InlineData("not-an-email", "password")]
    [InlineData("user@example.test", "")]
    [InlineData("user@example.test", " ")]
    [InlineData("user@example.test", null)]
    public async Task InvalidLoginEntriesReturnBadRequest(string? email, string? password)
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { Email = email, Password = password });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False((await response.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("accessToken", out _));
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("signature")]
    [InlineData("missing-user")]
    [InlineData("missing-access")]
    [InlineData("mismatched-owner")]
    [InlineData("malformed-user")]
    [InlineData("malformed-access")]
    [InlineData("no-user")]
    [InlineData("no-access")]
    [InlineData("no-credential-version")]
    [InlineData("issuer")]
    [InlineData("audience")]
    public async Task InvalidJwtCannotAuthenticateEvenWithLegacyAccessHeader(string scenario)
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = new IntegrationApiFactory(database, "Development");
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var commerce = await db.AccesosUsuario.SingleAsync(x => x.ComercioId != null);
        var operatorAccess = await db.AccesosUsuario.SingleAsync(x => x.OperadorId != null);
        var user = scenario switch
        {
            "missing-user" => Guid.NewGuid().ToString(),
            "mismatched-owner" => operatorAccess.UsuarioId.ToString(),
            "malformed-user" => "not-a-guid", "no-user" => null,
            _ => commerce.UsuarioId.ToString()
        };
        var access = scenario switch
        {
            "missing-access" => Guid.NewGuid().ToString(),
            "malformed-access" => "not-a-guid", "no-access" => null,
            _ => commerce.Id.ToString()
        };
        using var request = Request(Token(user, access, scenario));
        request.Headers.Add("X-NexoRuta-Acceso", commerce.Id.ToString());
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("role")]
    [InlineData("permission")]
    public async Task DatabaseChangesRevokePreviouslyIssuedJwtDespiteForgedPermissionClaims(string change)
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var commerce = await db.AccesosUsuario.SingleAsync(x => x.ComercioId != null);
        var user = await db.Usuarios.SingleAsync(x => x.Id == commerce.UsuarioId);
        var issued = await Login(client, user.Email);
        using (var accepted = await client.SendAsync(Request(issued)))
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        if (change == "role")
        {
            var role = new Rol("no_permissions", "No permissions", null, commerce.ComercioId);
            db.Roles.Add(role);
            commerce.AsignarRol(role);
        }
        else
        {
            var permission = await db.Permisos.SingleAsync(x => x.Codigo == "envios.consultar");
            db.RolesPermisos.Remove(await db.RolesPermisos.SingleAsync(
                x => x.RolId == commerce.RolId && x.PermisoId == permission.Id));
        }
        await db.SaveChangesAsync();
        using (var denied = await client.SendAsync(Request(issued)))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var forged = await client.SendAsync(Request(Token(commerce.UsuarioId.ToString(), commerce.Id.ToString(),
            additionalClaims: [new Claim("permission", "envios.consultar"),
                new Claim("permisos", "envios.consultar"), new Claim(ClaimTypes.Role, "propietario_comercio")])));
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        using var anonymous = await client.GetAsync("/api/envios");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Theory]
    [InlineData("usuarios.gestionar", true, true)]
    [InlineData("configuracion.gestionar", true, true)]
    [InlineData("envios.crear", true, false)]
    [InlineData("envios.consultar", true, true)]
    [InlineData("envios.gestionar", false, true)]
    public async Task EveryRegisteredPolicyUsesCurrentPostgresGrants(string policy, bool commerceAllowed, bool operatorAllowed)
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoRuta.Infrastructure.Persistence.NexoRutaDbContext>();
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        var previous = accessor.HttpContext;
        try
        {
            foreach (var commerce in new[] { true, false })
            {
                var access = await db.AccesosUsuario.SingleAsync(x => commerce ? x.ComercioId != null : x.OperadorId != null);
                if (!commerce && policy == "envios.crear")
                {
                    var roleId = access.RolId!.Value;
                    var permissionId = await db.Permisos.Where(x => x.Codigo == policy).Select(x => x.Id).SingleAsync();
                    if (!await db.RolesPermisos.AnyAsync(x => x.RolId == roleId && x.PermisoId == permissionId))
                    {
                        db.Add(new RolPermiso(roleId, permissionId));
                        await db.SaveChangesAsync();
                    }
                }
                var context = await scope.ServiceProvider.GetRequiredService<IAccesosUsuarioRepository>().ObtenerAsync(access.Id);
                var principal = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim(JwtRegisteredClaimNames.Sub, access.UsuarioId.ToString()),
                    new Claim(AccesoSeleccionado.ClaimAccesoId, access.Id.ToString()),
                    new Claim("permission", policy)], "Bearer"));
                accessor.HttpContext = new DefaultHttpContext();
                accessor.HttpContext.Items[typeof(ContextoUsuario)] = context;
                Assert.Equal(commerce ? commerceAllowed : operatorAllowed,
                    (await authorization.AuthorizeAsync(principal, null, policy)).Succeeded);
            }
        }
        finally { accessor.HttpContext = previous; }
    }

    [Theory]
    [InlineData("Development", "/api/envios")]
    [InlineData("Development", "/api/roles")]
    [InlineData("Development", "/api/usuarios/actual")]
    [InlineData("Development", "/api/operadores")]
    [InlineData("Testing", "/api/envios")]
    [InlineData("Production", "/api/envios")]
    public async Task LegacyAccessHeaderAloneNeverAuthenticates(string environment, string path)
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = new IntegrationApiFactory(database, environment);
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var access = await db.AccesosUsuario.SingleAsync(x => x.ComercioId != null);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-NexoRuta-Acceso", access.Id.ToString());
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AnonymousGlobalAccessCatalogIsRemoved()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = new IntegrationApiFactory(database, "Development");
        using var client = factory.CreateHttpClient();

        using var response = await client.GetAsync("/api/accesos?tipo=Comercio");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task JwtScopesShipmentCreationAndListingByCommerceAndOperator()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var commerce = await db.AccesosUsuario.SingleAsync(x => x.ComercioId != null);
        var operatorAccess = await db.AccesosUsuario.SingleAsync(x => x.OperadorId != null);
        var secondOperator = new Operador("Second operator");
        var secondCommerce = new Comercio("Second commerce");
        var secondUser = new Usuario("second@example.test", AuthenticationTestData.Hash("second@example.test"));
        var secondRole = new Rol("second", "Second commerce role", null, secondCommerce.Id);
        var secondAccess = new AccesoUsuario(secondUser.Id, null, secondCommerce.Id, secondRole);
        db.AddRange(secondOperator, secondCommerce, secondUser, secondRole, secondAccess);
        foreach (var permission in await db.Permisos.Where(x => x.Codigo == "envios.crear" || x.Codigo == "envios.consultar").ToListAsync())
            db.Add(new RolPermiso(secondRole.Id, permission.Id));
        await db.SaveChangesAsync();
        var commerceJwt = await Login(client, (await db.Usuarios.SingleAsync(x => x.Id == commerce.UsuarioId)).Email);
        var operatorJwt = await Login(client, (await db.Usuarios.SingleAsync(x => x.Id == operatorAccess.UsuarioId)).Email);
        var secondJwt = await Login(client, secondUser.Email);
        async Task<Guid> Create(string token, Guid operatorId)
        {
            using var request = Request(token, HttpMethod.Post);
            request.Content = JsonContent.Create(new { OperadorId = operatorId, DestinatarioNombre = "Recipient", DestinatarioEmail = "destinatario@example.test",
                Direccion = "Address 123", CodigoBulto = Guid.NewGuid().ToString("N"), PesoGramos = 100,
                LargoCentimetros = 10, AnchoCentimetros = 10, AltoCentimetros = 10 });
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        }
        var first = await Create(commerceJwt, operatorAccess.OperadorId!.Value);
        var second = await Create(commerceJwt, secondOperator.Id);
        var other = await Create(secondJwt, operatorAccess.OperadorId.Value);
        foreach (var (token, expected) in new[] { (commerceJwt, new[] { first, second }),
            (operatorJwt, new[] { first, other }), (secondJwt, new[] { other }) })
        {
            using var response = await client.SendAsync(Request(token));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var rows = await response.Content.ReadFromJsonAsync<JsonElement[]>();
            Assert.Equal(expected.Order(), rows!.Select(x => x.GetProperty("id").GetGuid()).Order());
        }
        using var forbiddenRequest = Request(operatorJwt, HttpMethod.Post);
        forbiddenRequest.Content = JsonContent.Create(new { OperadorId = operatorAccess.OperadorId,
            DestinatarioNombre = "Recipient", DestinatarioEmail = "destinatario@example.test", Direccion = "Address 123", CodigoBulto = "DENIED",
            PesoGramos = 100, LargoCentimetros = 10, AnchoCentimetros = 10, AltoCentimetros = 10 });
        using var forbidden = await client.SendAsync(forbiddenRequest);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(3, await db.Envios.CountAsync());
    }

    [Fact]
    public async Task PublicTrackingIsUnchangedForAnonymousAndJwtClients()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var access = await db.AccesosUsuario.SingleAsync(x => x.ComercioId != null);
        var jwt = await Login(client, (await db.Usuarios.SingleAsync(x => x.Id == access.UsuarioId)).Email);
        var shipment = await SeguimientoTestData.CreateEnvioAsync(db);
        var link = await SeguimientoTestData.CreateActiveLinkAsync(db, shipment);
        using var anonymous = await client.GetAsync($"/api/public/seguimiento/{link}");
        Assert.Equal(HttpStatusCode.OK, anonymous.StatusCode);
        using var request = Request(jwt, path: $"/api/public/seguimiento/{link}");
        using var authenticated = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
        Assert.Equal(await anonymous.Content.ReadAsStringAsync(), await authenticated.Content.ReadAsStringAsync());
        using var unknown = await client.GetAsync($"/api/public/seguimiento/{new string('a', 43)}");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    private static HttpRequestMessage Request(string token, HttpMethod? method = null, string path = "/api/envios")
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<string> Login(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login", new
            { Email = email, Password = AuthenticationTestData.Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    private static string Token(string? user, string? access, string scenario = "valid", Claim[]? additionalClaims = null)
    {
        var claims = new List<Claim>();
        if (user is not null) claims.Add(new Claim(JwtRegisteredClaimNames.Sub, user));
        if (access is not null) claims.Add(new Claim(AccesoSeleccionado.ClaimAccesoId, access));
        claims.Add(new Claim(JwtSesionClaims.VersionSesion, "0"));
        if (scenario != "no-credential-version") claims.Add(new Claim(JwtSesionClaims.VersionCredencial, "0"));
        if (additionalClaims is not null) claims.AddRange(additionalClaims);
        var key = scenario == "signature" ? System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)
            : Convert.FromBase64String(AuthenticationTestData.SigningKey);
        var now = DateTime.UtcNow;
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: scenario == "issuer" ? "wrong-issuer" : AuthenticationTestData.Issuer,
            audience: scenario == "audience" ? "wrong-audience" : AuthenticationTestData.Audience,
            claims: claims, notBefore: now.AddMinutes(-10),
            expires: scenario == "expired" ? now.AddMinutes(-2) : now.AddMinutes(15),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256)));
    }
}
