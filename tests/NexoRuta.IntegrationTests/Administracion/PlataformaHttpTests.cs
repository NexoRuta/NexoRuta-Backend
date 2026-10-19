using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.IdentityModel.Tokens;
using NexoRuta.Api.Core.Security;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Administracion.Autenticacion;
using NexoRuta.Infrastructure.Administracion.Repositories;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguimiento;

namespace NexoRuta.IntegrationTests.Administracion;

public sealed class PlataformaHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string PlatformEmail = "platform@example.test";
    private const string Password = AuthenticationTestData.Password;

    [Fact]
    public async Task PlatformSelfContextAndPermissionsDoNotCreateAnOrganizationContext()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = EmptyFactory(database);
        using var client = factory.CreateHttpClient();
        await Initialize(database);
        await Login(client, PlatformEmail);
        using var response = await client.GetAsync("/api/usuarios/actual");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var context = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Plataforma", context.GetProperty("tipo").GetString());
        Assert.Equal(PlatformEmail, context.GetProperty("usuarioEmail").GetString());
        foreach (var key in new[] { "operadorId", "comercioId", "rolId", "rolCodigo", "tipoAcceso" })
            Assert.Equal(JsonValueKind.Null, context.GetProperty(key).ValueKind);
        Assert.Equal(new[] { RolPlataforma.GestionarOrganizaciones }, await client.GetFromJsonAsync<string[]>("/api/usuarios/actual/permisos"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/envios")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PatchAsJsonAsync($"/api/usuarios/{Guid.NewGuid()}/email",
            new { Email = "platform-edit@example.test", Version = 0 })).StatusCode);
        await using var db = database.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("UPDATE \"RolesPlataforma\" SET \"PuedeGestionarOrganizaciones\" = false");
        Assert.Empty((await client.GetFromJsonAsync<string[]>("/api/usuarios/actual/permisos"))!);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/operadores")).StatusCode);
    }

    [Fact]
    public async Task AdministrativeEndpointsRequireAuthentication()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        foreach (var kind in new[] { "operadores", "comercios" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/admin/{kind}")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/admin/{kind}/{Guid.NewGuid()}")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync($"/api/admin/{kind}", null)).StatusCode);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrganizationAdministratorsCannotUsePlatformOrGrantPlatformPermissions(bool commerce)
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = database.CreateDbContext();
        var access = await db.AccesosUsuario.SingleAsync(x => commerce ? x.ComercioId != null : x.OperadorId != null);
        await client.SetBearerForAccessAsync(database, access.Id);
        // Grant configuration management to the demo commerce actor so the grant validation itself is exercised.
        var permission = await db.Permisos.SingleAsync(x => x.Codigo == "configuracion.gestionar");
        if (!await db.RolesPermisos.AnyAsync(x => x.RolId == access.RolId && x.PermisoId == permission.Id))
        {
            db.Add(new RolPermiso(access.RolId!.Value, permission.Id));
            await db.SaveChangesAsync();
        }
        foreach (var kind in new[] { "operadores", "comercios" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/admin/{kind}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/admin/{kind}/{Guid.NewGuid()}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/admin/{kind}", Request("Denied", "denied@example.test"))).StatusCode);
        }
        var customRole = new Rol("platform-admin", "Platform administrator", access.OperadorId, access.ComercioId);
        db.Add(customRole);
        await db.SaveChangesAsync();
        using var grant = await client.PutAsJsonAsync($"/api/roles/{customRole.Id}/permisos", new { Permisos = new[] { RolPlataforma.GestionarOrganizaciones } });
        Assert.Equal(HttpStatusCode.BadRequest, grant.StatusCode);
        using var create = await client.PostAsJsonAsync("/api/usuarios", new
        {
            Email = "not-platform@example.test", Password, RolId = customRole.Id,
            EsPlataforma = true, TipoAcceso = "Plataforma", Permisos = new[] { RolPlataforma.GestionarOrganizaciones }
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        await Login(client, "not-platform@example.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/operadores")).StatusCode);
        Assert.Empty(await db.AccesosPlataforma.ToListAsync());
        Assert.Empty(await db.RolesPlataforma.ToListAsync());
    }

    [Theory]
    [InlineData("operadores", TipoAccesoUsuario.Operador)]
    [InlineData("comercios", TipoAccesoUsuario.Comercio)]
    public async Task ProvisioningCreatesProtectedAdministratorAndScopeWithoutLeakingCredentials(string kind, TipoAccesoUsuario type)
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = EmptyFactory(database);
        using var client = factory.CreateHttpClient();
        await Initialize(database);
        await Login(client, PlatformEmail);
        using var response = await client.PostAsJsonAsync($"/api/admin/{kind}", Request("  New organization  ", "  ADMIN@example.test  "));
        // Normalization belongs to the shared application rule; the HTTP email annotation also accepts trimmed input.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Password, raw);
        Assert.DoesNotContain("hash", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase);
        var created = (await response.Content.ReadFromJsonAsync<AltaOrganizacion>())!;
        Assert.Equal("New organization", created.Organizacion.Nombre);
        Assert.Equal($"/api/admin/{kind}/{created.Organizacion.Id}", response.Headers.Location!.ToString());
        await using var db = database.CreateDbContext();
        var user = await db.Usuarios.SingleAsync(x => x.Id == created.UsuarioId);
        Assert.Equal("admin@example.test", user.Email);
        Assert.Equal(PasswordVerificationResult.Success,
            new PasswordHasher<string>().VerifyHashedPassword(user.Email, user.PasswordHash!, Password));
        var access = await db.AccesosUsuario.SingleAsync();
        Assert.True(access.Activo);
        Assert.True(access.EsPropietario);
        Assert.Equal(created.AccesoId, access.Id);
        Assert.Equal(created.RolId, access.RolId);
        Assert.Equal(created.Organizacion.Id, access.OrganizacionId);
        var role = await db.Roles.SingleAsync();
        Assert.True(role.EsProtegidoSistema);
        Assert.Equal(type, role.TipoOrganizacion);
        var grants = await (from grant in db.RolesPermisos join p in db.Permisos on grant.PermisoId equals p.Id
                            where grant.RolId == role.Id orderby p.Codigo select p.Codigo).ToArrayAsync();
        Assert.Equal(CatalogoPermisos.Para(type).Select(x => x.Codigo).Order(), grants);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(response.Headers.Location)).StatusCode);
        await Login(client, "ADMIN@example.test");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/usuarios/actual")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/envios")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/admin/{kind}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/roles/{role.Id}", new { Nombre = "Changed" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/roles/{role.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/roles/{role.Id}/permisos", new { Permisos = Array.Empty<string>() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/roles/accesos/{access.Id}", new { RolId = (Guid?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PatchAsJsonAsync($"/api/usuarios/{user.Id}/estado", new { Activo = false })).StatusCode);
    }

    [Fact]
    public async Task PlatformTokensAreNotOrganizationContextsAndRevocationIsImmediate()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = new IntegrationApiFactory(database, "Development",
            settings: new Dictionary<string, string?> { ["DatabaseInitialization:SeedData"] = "false" });
        using var client = factory.CreateHttpClient();
        await Initialize(database);
        await Login(client, PlatformEmail);
        var token = client.DefaultRequestHeaders.Authorization!.Parameter!;
        Assert.Equal("plataforma", new JwtSecurityTokenHandler().ReadJwtToken(token).Claims.Single(x => x.Type == AccesoSeleccionado.ClaimAmbito).Value);
        foreach (var route in new[] { "/api/envios", "/api/usuarios", "/api/roles", "/api/operadores" })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(route)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/envios", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/envios/{Guid.NewGuid()}/seguimiento")).StatusCode);
        await using var db = database.CreateDbContext();
        var access = await db.AccesosPlataforma.SingleAsync();
        access.CambiarEstado(false);
        await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/operadores")).StatusCode);
        access.CambiarEstado(true);
        await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/operadores")).StatusCode);
        await Login(client, PlatformEmail);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/admin/operadores")).StatusCode);
    }

    [Fact]
    public async Task TokenScopeCannotBeReinterpretedEvenWithAnOtherwiseValidSignature()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = database.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await Initialize(database);
        await using var db = database.CreateDbContext();
        var platform = await db.AccesosPlataforma.SingleAsync();
        var organization = await db.AccesosUsuario.FirstAsync();
        string Signed(Guid user, Guid access, string? scope)
        {
            var claims = new List<Claim>
            {
                new(JwtRegisteredClaimNames.Sub, user.ToString()), new(AccesoSeleccionado.ClaimAccesoId, access.ToString()),
                new(JwtSesionClaims.VersionSesion, "0")
            };
            if (scope is not null) claims.Add(new(AccesoSeleccionado.ClaimAmbito, scope));
            return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(AuthenticationTestData.Issuer,
                AuthenticationTestData.Audience, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials:
                new SigningCredentials(new SymmetricSecurityKey(Convert.FromBase64String(AuthenticationTestData.SigningKey)), SecurityAlgorithms.HmacSha256)));
        }
        foreach (var token in new[]
        {
            Signed(organization.UsuarioId, organization.Id, "plataforma"),
            Signed(platform.UsuarioId, platform.AccesoId, null),
            Signed(platform.UsuarioId, platform.AccesoId, "Operador"),
            Signed(organization.UsuarioId, platform.AccesoId, "plataforma")
        })
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/operadores")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/envios")).StatusCode);
        }
    }

    [Fact]
    public async Task CreatedCommerceCanSelectDifferentOperatorsWhileEachActorSeesOnlyItsShipments()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = EmptyFactory(database);
        using var client = factory.CreateHttpClient();
        await Initialize(database);
        await Login(client, PlatformEmail);
        async Task<AltaOrganizacion> Create(string kind, string email)
        {
            using var response = await client.PostAsJsonAsync($"/api/admin/{kind}", Request("Independent", email));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<AltaOrganizacion>())!;
        }
        var op1 = await Create("operadores", "operator1@example.test");
        var op2 = await Create("operadores", "operator2@example.test");
        var commerce1 = await Create("comercios", "commerce1@example.test");
        var commerce2 = await Create("comercios", "commerce2@example.test");
        // Only four explicit logins after the platform login, within the existing five/minute limiter.
        await Login(client, "commerce1@example.test");
        var available = await client.GetFromJsonAsync<JsonElement[]>("/api/operadores");
        Assert.Equal(2, available!.Length);
        async Task Ship(Guid op)
        {
            using var response = await client.PostAsJsonAsync("/api/envios", new
            {
                OperadorId = op, DestinatarioNombre = "Recipient", DestinatarioEmail = "destinatario@example.test", Direccion = "Delivery address", CodigoBulto = "BOX",
                PesoGramos = 100, LargoCentimetros = 10, AnchoCentimetros = 10, AltoCentimetros = 10
            });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
        await Ship(op1.Organizacion.Id);
        await Ship(op2.Organizacion.Id);
        Assert.Equal(2, (await client.GetFromJsonAsync<JsonElement[]>("/api/envios"))!.Length);
        await Login(client, "commerce2@example.test");
        Assert.Empty((await client.GetFromJsonAsync<JsonElement[]>("/api/envios"))!);
        await Ship(op2.Organizacion.Id);
        Assert.Single((await client.GetFromJsonAsync<JsonElement[]>("/api/envios"))!);
        await Login(client, "operator1@example.test");
        Assert.Single((await client.GetFromJsonAsync<JsonElement[]>("/api/envios"))!);
        await Login(client, "operator2@example.test");
        Assert.Equal(2, (await client.GetFromJsonAsync<JsonElement[]>("/api/envios"))!.Length);
        await using var db = database.CreateDbContext();
        Assert.Equal(3, await db.Envios.CountAsync());
        Assert.Equal(2, await db.Envios.CountAsync(x => x.ComercioId == commerce1.Organizacion.Id));
        Assert.Equal(1, await db.Envios.CountAsync(x => x.ComercioId == commerce2.Organizacion.Id));
    }

    [Theory]
    [InlineData("operadores")]
    [InlineData("comercios")]
    public async Task DuplicateEmailAndConcurrentRequestsCannotLeavePartialOrganizations(string kind)
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = EmptyFactory(database);
        using var client = factory.CreateHttpClient();
        await Initialize(database);
        await Login(client, PlatformEmail);
        var requests = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => client.PostAsJsonAsync($"/api/admin/{kind}", Request("Same name allowed", "same@example.test"))));
        Assert.Single(requests, x => x.StatusCode == HttpStatusCode.Created);
        Assert.Equal(3, requests.Count(x => x.StatusCode == HttpStatusCode.Conflict));
        foreach (var response in requests) response.Dispose();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/admin/{kind}", Request("Duplicate", "SAME@example.test"))).StatusCode);
        // A different organization may have the same name, including in the same organization kind.
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync($"/api/admin/{kind}", Request("Same name allowed", "other@example.test"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/admin/{(kind == "operadores" ? "comercios" : "operadores")}", Request("Cross-kind", "same@example.test"))).StatusCode);
        await using var db = database.CreateDbContext();
        Assert.Equal(2, await db.Operadores.CountAsync() + await db.Comercios.CountAsync());
        Assert.Equal(2, await db.Roles.CountAsync());
        Assert.Equal(2, await db.AccesosUsuario.CountAsync());
        Assert.Equal(3, await db.Usuarios.CountAsync());
        var user = await db.Usuarios.SingleAsync(x => x.Email == "same@example.test");
        Assert.Equal(PasswordVerificationResult.Success, new PasswordHasher<string>().VerifyHashedPassword(user.Email, user.PasswordHash!, Password));
    }

    [Theory]
    [InlineData("operadores")]
    [InlineData("comercios")]
    public async Task ListingsHaveStablePaginationAndValidationRejectsManipulatedInputs(string kind)
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = EmptyFactory(database);
        using var client = factory.CreateHttpClient();
        await Initialize(database);
        await Login(client, PlatformEmail);
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var response = await client.PostAsJsonAsync($"/api/admin/{kind}", Request("Same", $"page{i}@example.test"));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            ids.Add((await response.Content.ReadFromJsonAsync<AltaOrganizacion>())!.Organizacion.Id);
        }
        var page1 = await client.GetFromJsonAsync<PaginaOrganizaciones>($"/api/admin/{kind}?pagina=1&tamanoPagina=2");
        var page2 = await client.GetFromJsonAsync<PaginaOrganizaciones>($"/api/admin/{kind}?pagina=2&tamanoPagina=2");
        Assert.Equal(3, page1!.Total);
        Assert.Equal(ids.Order(), page1.Items.Concat(page2!.Items).Select(x => x.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/admin/{kind}/{Guid.NewGuid()}")).StatusCode);
        foreach (var query in new[] { "pagina=0", "tamanoPagina=0", "tamanoPagina=101", "pagina=2147483647" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/admin/{kind}?{query}")).StatusCode);
        foreach (var body in new object[]
        {
            Request("", "valid@example.test"), Request(new string('x', 161), "valid@example.test"),
            Request("Name", "bad"), new { Nombre = "Name", Administrador = new { Email = "valid@example.test", Password = "short" } },
            new { Nombre = "Name", Administrador = (object?)null },
            new { Nombre = "Name", Administrador = new { Email = "valid@example.test", Password, EsPlataforma = true } },
            new { Nombre = "Name", Administrador = new { Email = "valid@example.test", Password }, OperadorId = Guid.NewGuid() }
        }) Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/admin/{kind}", body)).StatusCode);
        await using var db = database.CreateDbContext();
        Assert.Equal(3, await db.AccesosUsuario.CountAsync());
    }

    [Theory]
    [InlineData(TipoAccesoUsuario.Operador)]
    [InlineData(TipoAccesoUsuario.Comercio)]
    public async Task FailureAfterSavingAllEntitiesRollsBackIncludingNewCatalogEntries(TipoAccesoUsuario type)
    {
        var database = await postgres.CreateDatabaseAsync();
        await using (var db = database.CreateDbContext(new FailAfterSave()))
            await Assert.ThrowsAsync<InvalidOperationException>(() => Repository(db).CrearAsync(type, "Rollback", "rollback@example.test", Password, default));
        await using var verification = database.CreateDbContext();
        Assert.Empty(await verification.Operadores.ToListAsync());
        Assert.Empty(await verification.Comercios.ToListAsync());
        Assert.Empty(await verification.Usuarios.ToListAsync());
        Assert.Empty(await verification.Roles.ToListAsync());
        Assert.Empty(await verification.Permisos.ToListAsync());
        Assert.Empty(await verification.RolesPermisos.ToListAsync());
        Assert.Empty(await verification.AccesosUsuario.ToListAsync());
    }

    [Fact]
    public async Task InitializerNeverOverwritesOrElevatesAndConcurrentInitializationCreatesOnlyOneAccount()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using (var db = database.CreateDbContext())
        {
            var existing = new Usuario("existing@example.test", AuthenticationTestData.Hash("existing@example.test"));
            db.Add(existing);
            await db.SaveChangesAsync();
            var hash = existing.PasswordHash;
            await Assert.ThrowsAsync<CorreoYaRegistradoException>(() => Repository(db).InicializarAsync("EXISTING@example.test", "A-different-password-2026!"));
            Assert.Equal(hash, (await db.Usuarios.AsNoTracking().SingleAsync()).PasswordHash);
            Assert.Empty(await db.AccesosPlataforma.ToListAsync());
        }
        async Task<bool> Attempt(string email)
        {
            await using var db = database.CreateDbContext();
            try { await Repository(db).InicializarAsync(email, Password); return true; }
            catch (InvalidOperationException) { return false; }
        }
        var results = await Task.WhenAll(Attempt(PlatformEmail), Attempt("second@example.test"));
        Assert.Single(results, x => x);
        await using var verification = database.CreateDbContext();
        Assert.Single(await verification.AccesosPlataforma.ToListAsync());
        Assert.True((await verification.RolesPlataforma.SingleAsync()).EsProtegidoSistema);
        Assert.True((await verification.RolesPlataforma.SingleAsync()).PuedeGestionarOrganizaciones);
        var before = await verification.Usuarios.OrderBy(x => x.Id).Select(x => new { x.Id, x.Email, x.PasswordHash }).ToArrayAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Repository(verification).InicializarAsync("third@example.test", Password));
        Assert.Equal(before, await verification.Usuarios.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.Email, x.PasswordHash }).ToArrayAsync());
        Assert.Empty(await verification.AccesosUsuario.ToListAsync());
    }

    [Fact]
    public async Task UpgradeFromPreviousSchemaPreservesIdentitiesRolesGrantsAndShipments()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var db = database.CreateDbContext();
        await db.Database.MigrateAsync();
        var shipment = await SeguimientoTestData.CreateEnvioAsync(db);
        var role = new Rol("legacy", "Legacy", shipment.OperadorId, null);
        role.ProtegerComoRolSistema();
        var access = new AccesoUsuario(shipment.CreadoPorUsuarioId, shipment.OperadorId, null, role);
        var permission = new Permiso("envios.consultar", "View shipments");
        db.AddRange(role, permission, new RolPermiso(role.Id, permission.Id));
        await db.SaveChangesAsync();
        db.AccesosUsuario.Add(access);
        await db.SaveChangesAsync();
        var hash = (await db.Usuarios.SingleAsync()).PasswordHash;
        await db.Database.MigrateAsync("20261016021011_ProtectSystemAdministratorRoles");
        await HistoricRecipientEmail.MigrateAsync(db);
        db.ChangeTracker.Clear();
        Assert.Equal(shipment.Id, (await db.Envios.SingleAsync()).Id);
        Assert.Equal(access.Id, (await db.AccesosUsuario.SingleAsync()).Id);
        Assert.Equal(shipment.CreadoPorUsuarioId, (await db.Usuarios.SingleAsync()).Id);
        Assert.Equal(hash, (await db.Usuarios.SingleAsync()).PasswordHash);
        Assert.Equal(role.Id, (await db.Roles.SingleAsync()).Id);
        Assert.True((await db.Roles.SingleAsync()).EsProtegidoSistema);
        Assert.Equal(permission.Id, (await db.RolesPermisos.SingleAsync()).PermisoId);
        Assert.Empty(await db.AccesosPlataforma.ToListAsync());
        Assert.Empty(await db.RolesPlataforma.ToListAsync());
        Assert.True(db.Model.FindEntityType(typeof(AccesoUsuario))!.GetIndexes().Single(x => x.Properties.Select(p => p.Name).SequenceEqual(["UsuarioId"])).IsUnique);
        var duplicate = new Usuario((await db.Usuarios.SingleAsync()).Email.ToUpperInvariant(), "unchanged-test-hash");
        db.Add(duplicate);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    private sealed class FailAfterSave : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default)
            => throw new InvalidOperationException("Injected failure before transaction commit.");
    }

    private static EfPlataformaRepository Repository(NexoRutaDbContext db)
        => new(db, AuthenticationTestData.UserManager(db));
    private static async Task Initialize(IntegrationDatabase database)
    {
        await using var db = database.CreateDbContext();
        await Repository(db).InicializarAsync(PlatformEmail, Password);
    }
    private static IntegrationApiFactory EmptyFactory(IntegrationDatabase database)
        => new(database, settings: new Dictionary<string, string?> { ["DatabaseInitialization:SeedData"] = "false" });
    private static object Request(string name, string email) => new { Nombre = name, Administrador = new { Email = email, Password } };
    private static async Task Login(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { Email = email, Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}
