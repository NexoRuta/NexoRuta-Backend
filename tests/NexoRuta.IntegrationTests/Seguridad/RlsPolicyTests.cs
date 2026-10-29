using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Seguimiento;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.Infrastructure.Outbox;
using NexoRuta.Infrastructure.Administracion.Autenticacion;
using NexoRuta.Infrastructure.Administracion;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.Application.Seguimiento.Services;
using NexoRuta.Application.Seguimiento.UseCases;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NexoRuta.Infrastructure.Seguimiento.Consultas;
using NexoRuta.Infrastructure.Seguimiento.Processing;
using NexoRuta.Infrastructure.Seguimiento.Repositories;
using NexoRuta.Infrastructure.Seguimiento.Security;
using Xunit;

namespace NexoRuta.IntegrationTests.Seguridad;

public sealed class RlsPolicyTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string PreviousMigration = "20261017182354_AddIdentityUserStore";
    private static readonly string[] ProtectedTables =
    [
        "AccesosUsuario", "AccesosPlataforma", "RolesPlataforma", "RolesPlataformaPermisos",
        "Operadores", "Comercios", "SolicitudesBajaOrganizacion", "SolicitudesReactivacionOrganizacion",
        "CambiosEstadoOrganizacion", "Destinatarios", "Direcciones", "Envios", "Bultos",
        "EnlacesSeguimiento", "EntregasSeguimiento", "OutboxMessages", "ProcessedMessages",
        "Roles", "Permisos", "RolesPermisos"
    ];

    [Fact]
    public async Task BusinessTablesAreForcedThroughRlsAndRuntimeRolesAreRestricted()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();

        await using (var command = new NpgsqlCommand("""
            SELECT c.relrowsecurity, c.relforcerowsecurity
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relname = @table AND c.relkind = 'r'
            """, connection))
        {
            var table = command.Parameters.Add("table", NpgsqlTypes.NpgsqlDbType.Text);
            foreach (var name in ProtectedTables)
            {
                table.Value = name;
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync(), $"Expected protected table {name} to exist.");
                Assert.True(reader.GetBoolean(0), $"RLS is not enabled for {name}.");
                Assert.True(reader.GetBoolean(1), $"RLS is not forced for {name}.");
            }
        }

        foreach (var roleName in new[] { "nexoruta_api", "nexoruta_worker" })
        {
            await using var command = new NpgsqlCommand("""
                SELECT r.rolsuper, r.rolbypassrls, r.rolcanlogin,
                       EXISTS (
                           SELECT 1 FROM pg_auth_members m
                           WHERE m.roleid = r.oid AND m.member = (SELECT oid FROM pg_roles WHERE rolname = current_user)
                       ) AS member
                FROM pg_roles r WHERE r.rolname = @role
                """, connection);
            command.Parameters.AddWithValue("role", roleName);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), $"Expected runtime role {roleName}.");
            Assert.False(reader.GetBoolean(0), $"Runtime role {roleName} is SUPERUSER.");
            Assert.False(reader.GetBoolean(1), $"Runtime role {roleName} has BYPASSRLS.");
            Assert.False(reader.GetBoolean(2), $"Runtime group {roleName} must not accept a direct login.");
            Assert.False(reader.GetBoolean(3), $"Database owner unexpectedly inherits runtime role {roleName}.");
        }

        await using (var identityTables = new NpgsqlCommand("""
            SELECT bool_or(c.relrowsecurity OR c.relforcerowsecurity)
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relname = ANY(@tables)
            """, connection))
        {
            identityTables.Parameters.AddWithValue("tables", new[] { "Usuarios", "AspNetUserClaims", "AspNetUserLogins", "AspNetUserTokens" });
            Assert.False((bool)(await identityTables.ExecuteScalarAsync())!, "Identity's global account store must remain queryable before tenant selection.");
        }

        await using (var functions = new NpgsqlCommand("""
            SELECT p.proname, p.proconfig, r.rolcanlogin, r.rolsuper, r.rolbypassrls,
                   EXISTS (
                       SELECT 1 FROM aclexplode(COALESCE(p.proacl, acldefault('f', p.proowner))) acl
                       WHERE acl.grantee = 0 AND acl.privilege_type = 'EXECUTE') AS public_execute
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            JOIN pg_roles r ON r.oid = p.proowner
            WHERE n.nspname = 'nexoruta_app'
            ORDER BY p.proname
            """, connection))
        await using (var reader = await functions.ExecuteReaderAsync())
        {
            var count = 0;
            while (await reader.ReadAsync())
            {
                count++;
                Assert.False(reader.GetBoolean(2), $"Function owner for {reader.GetString(0)} must not accept login.");
                Assert.False(reader.GetBoolean(3), $"Function owner for {reader.GetString(0)} is SUPERUSER.");
                Assert.False(reader.GetBoolean(4), $"Function owner for {reader.GetString(0)} has BYPASSRLS.");
                Assert.False(reader.GetBoolean(5), $"PUBLIC must not execute {reader.GetString(0)}.");
                var config = reader.GetFieldValue<string[]>(1);
                var searchPath = Assert.Single(config, value => value.StartsWith("search_path=", StringComparison.Ordinal));
                Assert.Contains(searchPath.Replace(" ", "", StringComparison.Ordinal),
                    new[] { "search_path=pg_catalog", "search_path=pg_catalog,nexoruta_app" });
            }
            Assert.True(count >= 15, "Expected the scoped context and security-definer functions to be installed.");
        }
    }

    [Fact]
    public async Task RestrictedApiRoleCannotSeeRowsWithoutAnActorContext()
    {
        var database = await postgres.CreateDatabaseAsync();
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        const string testRole = "rls_test_api";
        await using (var owner = new NpgsqlConnection(database.ConnectionString))
        {
            await owner.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE ROLE {testRole} LOGIN PASSWORD '{password}' NOSUPERUSER NOBYPASSRLS; GRANT nexoruta_api TO {testRole};", owner);
            await create.ExecuteNonQueryAsync();
        }

        var restricted = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Username = testRole,
            Password = password,
            Pooling = false
        };
        await using var connection = new NpgsqlConnection(restricted.ConnectionString);
        await connection.OpenAsync();
        await using var verifyRole = new NpgsqlCommand("""
            SELECT current_user, r.rolsuper, r.rolbypassrls
            FROM pg_roles r WHERE r.rolname = current_user
            """, connection);
        await using (var reader = await verifyRole.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(testRole, reader.GetString(0));
            Assert.False(reader.GetBoolean(1));
            Assert.False(reader.GetBoolean(2));
        }

        await using var query = new NpgsqlCommand("SELECT count(*) FROM \"Envios\"", connection);
        Assert.Equal(0L, (long)(await query.ExecuteScalarAsync())!);
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await AssertDeniedAsync(connection, transaction,
                "INSERT INTO \"RolesPermisos\" (\"RolId\", \"PermisoId\") VALUES (gen_random_uuid(), gen_random_uuid())");
            await transaction.RollbackAsync();
        }
    }

    [Fact]
    public async Task PlatformContextDoesNotExposeShipmentOrTrackingPii()
    {
        var database = await postgres.CreateDatabaseAsync();
        var rows = await SeedTenantGraphAsync(database);
        var api = await CreateRestrictedConnectionAsync(database, "nexoruta_api");
        await using var connection = api.Connection;
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetContextAsync(connection, transaction, "actor_kind", "plataforma");
        await SetContextAsync(connection, transaction, "user_id", rows.UserA.ToString("D"));
        await SetContextAsync(connection, transaction, "platform_manage_organizations", "true");
        foreach (var table in new[] { "Envios", "Destinatarios", "Direcciones", "Bultos", "EnlacesSeguimiento", "EntregasSeguimiento" })
            Assert.Equal(0L, await CountAsync(connection, transaction, table));
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task TenantRowsAndShipmentChildrenAreScopedByBothShipmentPartiesAndLocalContextExpires()
    {
        var database = await postgres.CreateDatabaseAsync();
        var rows = await SeedTenantGraphAsync(database);
        var api = await CreateRestrictedConnectionAsync(database, "nexoruta_api");
        await using var connection = api.Connection;
        await connection.OpenAsync();
        var backendPid = (int)(await new NpgsqlCommand("SELECT pg_backend_pid()", connection).ExecuteScalarAsync())!;

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            Assert.Equal(0L, await CountAsync(connection, transaction, "Envios"));
            await SetContextAsync(connection, transaction, "actor_kind", "comercio");
            await SetContextAsync(connection, transaction, "org_id", rows.CommerceA.ToString("D"));
            await SetContextAsync(connection, transaction, "user_id", rows.UserA.ToString("D"));
            Assert.Equal(2L, await CountAsync(connection, transaction, "Envios"));
            Assert.Equal(2L, await CountAsync(connection, transaction, "Destinatarios"));
            Assert.Equal(2L, await CountAsync(connection, transaction, "Direcciones"));
            Assert.Equal(2L, await CountAsync(connection, transaction, "Bultos"));
            Assert.Equal(1L, await CountAsync(connection, transaction, "Comercios"));
            Assert.Equal(2L, await CountAsync(connection, transaction, "Operadores"));
            await AssertDeniedAsync(connection, transaction, $"DELETE FROM \"Envios\" WHERE \"Id\" = '{rows.ShipmentA1:D}'");
            await AssertDeniedAsync(connection, transaction, $"DELETE FROM \"Envios\" WHERE \"Id\" = '{rows.ShipmentB1:D}'");
            foreach (var id in new[] { rows.ShipmentA1, rows.ShipmentB1 })
            {
                await using var update = new NpgsqlCommand($"UPDATE \"Envios\" SET \"Estado\" = 'Admitido' WHERE \"Id\" = '{id:D}'", connection, transaction);
                Assert.Equal(0, await update.ExecuteNonQueryAsync());
            }
            await AssertDeniedAsync(connection, transaction, "INSERT INTO \"Envios\" (\"Id\") VALUES (gen_random_uuid())");
            await transaction.CommitAsync();
        }

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            Assert.Equal(backendPid, (int)(await new NpgsqlCommand("SELECT pg_backend_pid()", connection, transaction).ExecuteScalarAsync())!);
            Assert.Equal(0L, await CountAsync(connection, transaction, "Envios"));
            await SetContextAsync(connection, transaction, "actor_kind", "operador");
            await SetContextAsync(connection, transaction, "org_id", rows.OperatorB.ToString("D"));
            await SetContextAsync(connection, transaction, "user_id", rows.UserA.ToString("D"));
            Assert.Equal(2L, await CountAsync(connection, transaction, "Envios"));
            Assert.Equal(2L, await CountAsync(connection, transaction, "Destinatarios"));
            Assert.Equal(2L, await CountAsync(connection, transaction, "Direcciones"));
            Assert.Equal(2L, await CountAsync(connection, transaction, "Bultos"));
            Assert.Equal(2L, await CountAsync(connection, transaction, "Comercios"));
            await transaction.RollbackAsync();
        }

        await connection.CloseAsync();
        await connection.OpenAsync();
        await using var afterRollback = await connection.BeginTransactionAsync();
        Assert.Equal(backendPid, (int)(await new NpgsqlCommand("SELECT pg_backend_pid()", connection, afterRollback).ExecuteScalarAsync())!);
        Assert.Equal(0L, await CountAsync(connection, afterRollback, "Envios"));
        await afterRollback.RollbackAsync();
    }

    [Fact]
    public async Task ConcurrentTenantTransactionsDoNotShareActorContext()
    {
        var database = await postgres.CreateDatabaseAsync();
        var rows = await SeedTenantGraphAsync(database);
        var first = await CreateRestrictedConnectionAsync(database, "nexoruta_api");
        var second = await CreateRestrictedConnectionAsync(database, "nexoruta_api");
        await using var firstConnection = first.Connection;
        await using var secondConnection = second.Connection;
        await firstConnection.OpenAsync();
        await secondConnection.OpenAsync();
        await using var firstTx = await firstConnection.BeginTransactionAsync();
        await using var secondTx = await secondConnection.BeginTransactionAsync();
        await SetContextAsync(firstConnection, firstTx, "actor_kind", "comercio");
        await SetContextAsync(firstConnection, firstTx, "org_id", rows.CommerceA.ToString("D"));
        await SetContextAsync(secondConnection, secondTx, "actor_kind", "comercio");
        await SetContextAsync(secondConnection, secondTx, "org_id", rows.CommerceB.ToString("D"));

        var counts = await Task.WhenAll(
            CountAsync(firstConnection, firstTx, "Envios"),
            CountAsync(secondConnection, secondTx, "Envios"));
        Assert.Equal(2L, counts[0]);
        Assert.Equal(1L, counts[1]);
        await Task.WhenAll(firstTx.CommitAsync(), secondTx.CommitAsync());
    }

    [Fact]
    public async Task RestrictedRoleLockFunctionDoesNotRevealOrLockAnotherTenantRole()
    {
        var database = await postgres.CreateDatabaseAsync();
        var rows = await SeedTenantGraphAsync(database);
        Guid foreignRoleId;
        await using (var owner = database.CreateDbContext())
            foreignRoleId = await owner.Roles.Where(x => x.ComercioId == rows.CommerceB).Select(x => x.Id).SingleAsync();

        var api = await CreateRestrictedConnectionAsync(database, "nexoruta_api");
        await using var connection = api.Connection;
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetContextAsync(connection, transaction, "actor_kind", "comercio");
        await SetContextAsync(connection, transaction, "org_id", rows.CommerceA.ToString("D"));

        await using var lockForeign = new NpgsqlCommand(
            "SELECT nexoruta_app.lock_owned_role(@role_id)", connection, transaction);
        lockForeign.Parameters.AddWithValue("role_id", foreignRoleId);
        Assert.False((bool)(await lockForeign.ExecuteScalarAsync())!);
        Assert.Equal(0L, await ScalarCountAsync(connection, transaction,
            "SELECT count(*) FROM \"Roles\" WHERE \"Id\" = @role_id", foreignRoleId));
        await transaction.RollbackAsync();

        await using var verify = database.CreateDbContext();
        Assert.Equal("Admin", await verify.Roles.Where(x => x.Id == foreignRoleId).Select(x => x.Nombre).SingleAsync());
    }

    [Fact]
    public async Task AuthenticatedApiUsesPersistedAccessContextWithTheRestrictedApiLogin()
    {
        var database = await postgres.CreateDatabaseAsync();
        var rows = await SeedTenantGraphAsync(database);
        string loginEmail;
        string operatorEmail = $"operator-{Guid.NewGuid():N}@example.test";
        await using (var db = database.CreateDbContext())
        {
            var user = await db.Usuarios.SingleAsync(x => x.Id == rows.UserA);
            loginEmail = user.Email;
            user.ActualizarPasswordHash(new PasswordHasher<Usuario>().HashPassword(user, AuthenticationTestData.Password));
            var permission = new Permiso("envios.consultar", "Consult shipments");
            var roleId = await db.AccesosUsuario.Where(x => x.Id == rows.AccessA).Select(x => x.RolId!.Value).SingleAsync();
            db.Add(permission);
            db.Add(new RolPermiso(roleId, permission.Id));
            var operatorUser = new Usuario(operatorEmail);
            operatorUser.ActualizarPasswordHash(new PasswordHasher<Usuario>().HashPassword(operatorUser, AuthenticationTestData.Password));
            var operatorRole = new Rol("operator-admin", "Operator administrator", rows.OperatorA, null);
            db.AddRange(operatorUser, operatorRole, new AccesoUsuario(operatorUser.Id, rows.OperatorA, null, operatorRole));
            db.Add(new RolPermiso(operatorRole.Id, permission.Id));
            await db.SaveChangesAsync();
        }

        var apiRole = await CreateRestrictedConnectionAsync(database, "nexoruta_api");
        using var factory = new IntegrationApiFactory(database, "Testing", new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = apiRole.ConnectionString,
            ["DatabaseInitialization:ApplyMigrations"] = "false",
            ["DatabaseInitialization:SeedData"] = "false"
        }, replaceDbContext: false);
        using var client = factory.CreateHttpClient();
        using var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            Email = loginEmail,
            Password = AuthenticationTestData.Password
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new("Bearer", body.GetProperty("accessToken").GetString()!);
        client.DefaultRequestHeaders.Add("X-NexoRuta-Acceso", rows.AccessB.ToString("D"));

        var actual = await client.GetAsync("/api/usuarios/actual");
        Assert.Equal(HttpStatusCode.OK, actual.StatusCode);
        var shipments = await client.GetAsync("/api/envios");
        Assert.Equal(HttpStatusCode.OK, shipments.StatusCode);
        Assert.Equal(2, (await shipments.Content.ReadFromJsonAsync<JsonElement[]>())!.Length);

        using var operatorLogin = await client.PostAsJsonAsync("/api/auth/login", new
        {
            Email = operatorEmail,
            Password = AuthenticationTestData.Password
        });
        Assert.Equal(HttpStatusCode.OK, operatorLogin.StatusCode);
        var operatorBody = await operatorLogin.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new("Bearer", operatorBody.GetProperty("accessToken").GetString()!);
        var operatorShipments = await client.GetAsync("/api/envios");
        Assert.Equal(HttpStatusCode.OK, operatorShipments.StatusCode);
        Assert.Single((await operatorShipments.Content.ReadFromJsonAsync<JsonElement[]>())!);
    }

    [Fact]
    public async Task RestrictedApiRevalidatesPlatformAndUnassignedIdentitySessions()
    {
        var database = await postgres.CreateDatabaseAsync();
        const string platformEmail = "platform-rls@example.test";
        const string identityEmail = "identity-rls@example.test";
        await using (var db = database.CreateDbContext())
        {
            await AuthenticationTestData.PlatformRepository(db).InicializarAsync(platformEmail, AuthenticationTestData.Password);
            var identity = new Usuario(identityEmail);
            identity.ActualizarPasswordHash(new PasswordHasher<Usuario>().HashPassword(identity, AuthenticationTestData.Password));
            db.Add(identity);
            await db.SaveChangesAsync();
        }

        var apiRole = await CreateRestrictedConnectionAsync(database, "nexoruta_api");
        using var factory = new IntegrationApiFactory(database, "Testing", new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = apiRole.ConnectionString,
            ["DatabaseInitialization:ApplyMigrations"] = "false",
            ["DatabaseInitialization:SeedData"] = "false"
        }, replaceDbContext: false);
        using var client = factory.CreateHttpClient();

        foreach (var (email, expectedType, expectedEnvios) in new[]
        {
            (platformEmail, "Plataforma", HttpStatusCode.Forbidden),
            (identityEmail, "SinOrganizacion", HttpStatusCode.Forbidden)
        })
        {
            using var login = await client.PostAsJsonAsync("/api/auth/login", new
            {
                Email = email,
                Password = AuthenticationTestData.Password
            });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            var body = await login.Content.ReadFromJsonAsync<JsonElement>();
            client.DefaultRequestHeaders.Authorization = new("Bearer", body.GetProperty("accessToken").GetString()!);
            using var current = await client.GetAsync("/api/usuarios/actual");
            Assert.Equal(HttpStatusCode.OK, current.StatusCode);
            var actual = await current.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(expectedType, actual.GetProperty("tipo").GetString());
            Assert.Equal(expectedEnvios, (await client.GetAsync("/api/envios")).StatusCode);
        }
    }

    [Fact]
    public async Task RestrictedBootstrapRoleCreatesOnlyTheThreeConfiguredInitialAccountsIdempotently()
    {
        var database = await postgres.CreateDatabaseAsync();
        var restricted = await CreateRestrictedConnectionAsync(database, "nexoruta_bootstrap");
        var initial = new AccesoInicial("commerce-bootstrap@example.test", "Initial operator", "Initial commerce", "operator-bootstrap@example.test")
        {
            OperadorId = Guid.NewGuid(),
            ComercioId = Guid.NewGuid(),
            OperadorRolCodigo = "administrador_operador",
            ComercioRolCodigo = "propietario_comercio"
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(AuthenticationTestData.BootstrapSettings(initial))
            .Build();
        var options = new DbContextOptionsBuilder<NexoRutaDbContext>().UseNexoRutaPostgres(restricted.ConnectionString).Options;
        await using (var db = new NexoRutaDbContext(options))
        {
            var identityUsers = AuthenticationTestData.UserManager(db);
            var platform = new NexoRuta.Infrastructure.Administracion.Repositories.EfPlataformaRepository(db, identityUsers);
            await platform.InicializarAsync("platform-bootstrap@example.test", AuthenticationTestData.Password);
            var seeder = new DatosInicialesSeeder(db, initial, configuration, identityUsers, platform);
            await seeder.SeedAsync();
            await platform.InicializarAsync("platform-bootstrap@example.test", AuthenticationTestData.Password, sincronizarPasswordExistente: true);
            await seeder.SeedAsync();
        }

        await using var verify = database.CreateDbContext();
        Assert.Equal(3, await verify.Usuarios.CountAsync());
        Assert.Equal(2, await verify.AccesosUsuario.CountAsync());
        Assert.Single(await verify.AccesosPlataforma.ToListAsync());
        Assert.Single(await verify.Operadores.ToListAsync());
        Assert.Single(await verify.Comercios.ToListAsync());
    }

    [Fact]
    public async Task RestrictedApiRegistrationAndOwnCommerceCreationKeepTheIdentitySessionRules()
    {
        var database = await postgres.CreateDatabaseAsync();
        var apiRole = await CreateRestrictedConnectionAsync(database, "nexoruta_api");
        using var factory = new IntegrationApiFactory(database, "Testing", new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = apiRole.ConnectionString,
            ["DatabaseInitialization:ApplyMigrations"] = "false",
            ["DatabaseInitialization:SeedData"] = "false"
        }, replaceDbContext: false);
        using var client = factory.CreateHttpClient();
        const string email = "self-register-rls@example.test";
        using var registration = await client.PostAsJsonAsync("/api/auth/register", new
        {
            Email = email,
            Password = AuthenticationTestData.Password
        });
        Assert.Equal(HttpStatusCode.Created, registration.StatusCode);
        using var identityLogin = await client.PostAsJsonAsync("/api/auth/login", new
        {
            Email = email,
            Password = AuthenticationTestData.Password
        });
        Assert.Equal(HttpStatusCode.OK, identityLogin.StatusCode);
        var identityBody = await identityLogin.Content.ReadFromJsonAsync<JsonElement>();
        var identityToken = identityBody.GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", identityToken);
        using var createdOwnOrganization = await client.PostAsJsonAsync("/api/alta-propia/comercios", new { Nombre = "Self-registered commerce" });
        Assert.Equal(HttpStatusCode.Created, createdOwnOrganization.StatusCode);
        using var staleSession = await client.GetAsync("/api/usuarios/actual");
        Assert.Equal(HttpStatusCode.Unauthorized, staleSession.StatusCode);

        using var commerceLogin = await client.PostAsJsonAsync("/api/auth/login", new
        {
            Email = email,
            Password = AuthenticationTestData.Password
        });
        Assert.Equal(HttpStatusCode.OK, commerceLogin.StatusCode);
        var commerceBody = await commerceLogin.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new("Bearer", commerceBody.GetProperty("accessToken").GetString()!);
        var actual = await client.GetAsync("/api/usuarios/actual");
        Assert.Equal(HttpStatusCode.OK, actual.StatusCode);
        Assert.Equal("Comercio", (await actual.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tipo").GetString());

        const string operatorEmail = "self-register-operator-rls@example.test";
        using var operatorRegistration = await client.PostAsJsonAsync("/api/auth/register", new
        {
            Email = operatorEmail,
            Password = AuthenticationTestData.Password
        });
        Assert.Equal(HttpStatusCode.Created, operatorRegistration.StatusCode);
        using var operatorIdentityLogin = await client.PostAsJsonAsync("/api/auth/login", new
        {
            Email = operatorEmail,
            Password = AuthenticationTestData.Password
        });
        Assert.Equal(HttpStatusCode.OK, operatorIdentityLogin.StatusCode);
        var operatorIdentityBody = await operatorIdentityLogin.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new("Bearer", operatorIdentityBody.GetProperty("accessToken").GetString()!);
        using var createdOwnOperator = await client.PostAsJsonAsync("/api/alta-propia/operadores", new { Nombre = "Self-registered operator" });
        Assert.Equal(HttpStatusCode.Created, createdOwnOperator.StatusCode);
        using var staleOperatorSession = await client.GetAsync("/api/usuarios/actual");
        Assert.Equal(HttpStatusCode.Unauthorized, staleOperatorSession.StatusCode);
        using var operatorLogin = await client.PostAsJsonAsync("/api/auth/login", new
        {
            Email = operatorEmail,
            Password = AuthenticationTestData.Password
        });
        Assert.Equal(HttpStatusCode.OK, operatorLogin.StatusCode);
        var operatorBody = await operatorLogin.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new("Bearer", operatorBody.GetProperty("accessToken").GetString()!);
        using var operatorActual = await client.GetAsync("/api/usuarios/actual");
        Assert.Equal(HttpStatusCode.OK, operatorActual.StatusCode);
        Assert.Equal("Operador", (await operatorActual.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tipo").GetString());
    }

    [Fact]
    public async Task RestrictedCommerceCreatesOnlyAnAtomicShipmentForAnActiveOperator()
    {
        var database = await postgres.CreateDatabaseAsync();
        var rows = await SeedTenantGraphAsync(database);
        var api = await CreateRestrictedConnectionAsync(database, "nexoruta_api");
        await using var connection = api.Connection;
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var shipmentId = Guid.NewGuid();
        var recipientId = Guid.NewGuid();
        var addressId = Guid.NewGuid();
        var parcelId = Guid.NewGuid();
        await SetContextAsync(connection, transaction, "actor_kind", "comercio");
        await SetContextAsync(connection, transaction, "org_id", rows.CommerceA.ToString("D"));
        await SetContextAsync(connection, transaction, "user_id", rows.UserA.ToString("D"));
        await SetContextAsync(connection, transaction, "selected_operator_id", rows.OperatorB.ToString("D"));
        await SetContextAsync(connection, transaction, "shipment_id", shipmentId.ToString("D"));

        await using (var create = new NpgsqlCommand("""
            SELECT nexoruta_app.create_shipment(
                @shipment, @recipient, @address_id, @parcel, @recipient_name, @recipient_email, @address, @code,
                @weight, @length, @width, @height, @rules_version)
            """, connection, transaction))
        {
            create.Parameters.AddWithValue("shipment", shipmentId);
            create.Parameters.AddWithValue("recipient", recipientId);
            create.Parameters.AddWithValue("address_id", addressId);
            create.Parameters.AddWithValue("parcel", parcelId);
            create.Parameters.AddWithValue("recipient_name", "New recipient");
            create.Parameters.AddWithValue("recipient_email", "destinatario@example.test");
            create.Parameters.AddWithValue("address", "New address");
            create.Parameters.AddWithValue("code", "NEW-1");
            create.Parameters.AddWithValue("weight", 100m);
            create.Parameters.AddWithValue("length", 10m);
            create.Parameters.AddWithValue("width", 10m);
            create.Parameters.AddWithValue("height", 10m);
            create.Parameters.AddWithValue("rules_version", rows.OperatorB);
            Assert.Equal(shipmentId, (Guid)(await create.ExecuteScalarAsync())!);
        }
        await using (var outbox = new NpgsqlCommand("""
            INSERT INTO "OutboxMessages" ("Id", "OperadorId", "EnvioId", "Ambito", "ClaveAmbito", "Tipo", "VersionContrato", "Destino", "Prioridad", "Estado", "Payload", "EstadoEntrega", "Intentos", "CreadoEnUtc", "ProximoIntentoEnUtc")
            VALUES (@id, @operator, @shipment, 'Envio', @shipment, 'GenerarSeguimiento', 1, 'RabbitMq', 'Alta', 'Pendiente', '{}'::jsonb, NULL, 0, now(), now()),
                   (@event_id, @operator, @shipment, 'Envio', @shipment, 'EnvioCreado', 1, 'Kafka', 'Normal', 'Pendiente', '{}'::jsonb, NULL, 0, now(), now())
            """, connection, transaction))
        {
            outbox.Parameters.AddWithValue("id", Guid.NewGuid());
            outbox.Parameters.AddWithValue("event_id", Guid.NewGuid());
            outbox.Parameters.AddWithValue("operator", rows.OperatorB);
            outbox.Parameters.AddWithValue("shipment", shipmentId);
            await outbox.ExecuteNonQueryAsync();
        }
        await AssertDeniedAsync(connection, transaction, $$"""
            INSERT INTO "OutboxMessages" ("Id", "OperadorId", "EnvioId", "Ambito", "ClaveAmbito", "Tipo", "VersionContrato", "Destino", "Prioridad", "Estado", "Payload", "EstadoEntrega", "Intentos", "CreadoEnUtc", "ProximoIntentoEnUtc")
            VALUES ('{{Guid.NewGuid():D}}', '{{rows.OperatorB:D}}', '{{shipmentId:D}}', 'Envio', '{{shipmentId:D}}', 'OtherEvent', 1, 'Kafka', 'Normal', 'Pendiente', '{}'::jsonb, NULL, 0, now(), now())
            """);
        Assert.Equal(3L, await CountAsync(connection, transaction, "Envios"));
        Assert.Equal(3L, await CountAsync(connection, transaction, "Destinatarios"));
        await transaction.CommitAsync();

        await using var verify = new NpgsqlConnection(api.ConnectionString);
        await verify.OpenAsync();
        await using var query = new NpgsqlCommand("SELECT count(*) FROM \"Envios\" WHERE \"Id\" = @id", verify);
        query.Parameters.AddWithValue("id", shipmentId);
        Assert.Equal(0L, (long)(await query.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task PublicTrackingAndWorkerFunctionsReturnOnlyBoundedRows()
    {
        var database = await postgres.CreateDatabaseAsync();
        var rows = await SeedTenantGraphAsync(database);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var hash = GeneradorTokenSeguimiento.CalcularHash(token);
        var link = new EnlaceSeguimiento(rows.OperatorA, rows.ShipmentA1, hash,
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var jobId = Guid.NewGuid();
        await using (var owner = database.CreateDbContext())
        {
            owner.EnlacesSeguimiento.Add(link);
            owner.OutboxMessages.Add(OutboxMessage.ForShipment(rows.OperatorA, rows.ShipmentA1,
                OutboxMessageTypes.TrackingRequested, OutboxDestino.RabbitMq, "{}"));
            await owner.SaveChangesAsync();
            jobId = await owner.OutboxMessages.AsNoTracking().Where(x => x.EnvioId == rows.ShipmentA1).Select(x => x.Id).SingleAsync();
        }

        var api = await CreateRestrictedConnectionAsync(database, "nexoruta_api");
        await using (var connection = api.Connection)
        {
            await connection.OpenAsync();
            await using var valid = new NpgsqlCommand("SELECT * FROM nexoruta_app.public_tracking(@token)", connection);
            valid.Parameters.AddWithValue("token", token);
            Assert.Equal("Admitido", (string)(await valid.ExecuteScalarAsync())!);
            await using var invalid = new NpgsqlCommand("SELECT count(*) FROM nexoruta_app.public_tracking(@token)", connection);
            invalid.Parameters.AddWithValue("token", new string('0', 64));
            Assert.Equal(0L, (long)(await invalid.ExecuteScalarAsync())!);
            Assert.Equal(0L, await CountAsync(connection, null, "Envios"));
        }

        var worker = await CreateRestrictedConnectionAsync(database, "nexoruta_worker");
        await using var workerConnection = worker.Connection;
        await workerConnection.OpenAsync();
        var processorId = Guid.NewGuid();
        await using (var transaction = await workerConnection.BeginTransactionAsync())
        {
            await using var unclaimed = new NpgsqlCommand("SELECT nexoruta_app.begin_tracking_job(@message, @operator, @shipment)", workerConnection, transaction);
            unclaimed.Parameters.AddWithValue("message", jobId);
            unclaimed.Parameters.AddWithValue("operator", rows.OperatorA);
            unclaimed.Parameters.AddWithValue("shipment", rows.ShipmentA1);
            Assert.False((bool)(await unclaimed.ExecuteScalarAsync())!);
            await transaction.RollbackAsync();
        }
        await using (var claim = new NpgsqlCommand("SELECT * FROM nexoruta_app.claim_outbox(@processor, 'RabbitMq')", workerConnection))
        {
            claim.Parameters.AddWithValue("processor", processorId);
            await using var reader = await claim.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(new[] { "Id", "OperadorId", "EnvioId", "Ambito", "ClaveAmbito", "Tipo", "VersionContrato", "Destino", "Prioridad", "Payload", "EstadoEntrega", "Intentos", "CreadoEnUtc" },
                Enumerable.Range(0, reader.FieldCount).Select(reader.GetName));
            jobId = reader.GetGuid(reader.GetOrdinal("Id"));
            Assert.Equal(rows.OperatorA, reader.GetGuid(reader.GetOrdinal("OperadorId")));
            Assert.Equal(rows.ShipmentA1, reader.GetGuid(reader.GetOrdinal("EnvioId")));
        }
        await using (var transaction = await workerConnection.BeginTransactionAsync())
        {
            await using var beginJob = new NpgsqlCommand("SELECT nexoruta_app.begin_tracking_job(@message, @operator, @shipment)", workerConnection, transaction);
            beginJob.Parameters.AddWithValue("message", jobId);
            beginJob.Parameters.AddWithValue("operator", rows.OperatorA);
            beginJob.Parameters.AddWithValue("shipment", rows.ShipmentA1);
            Assert.True((bool)(await beginJob.ExecuteScalarAsync())!);
            Assert.Equal(1L, await CountAsync(workerConnection, transaction, "Envios"));
            await using var processed = new NpgsqlCommand("INSERT INTO \"ProcessedMessages\" (\"MessageId\", \"Consumer\", \"ProcessedAtUtc\") VALUES (@id, 'GenerarSeguimiento', now())", workerConnection, transaction);
            processed.Parameters.AddWithValue("id", jobId);
            await processed.ExecuteNonQueryAsync();
            Assert.Equal(1L, await CountAsync(workerConnection, transaction, "ProcessedMessages"));
            await transaction.CommitAsync();
        }
        await using (var finish = new NpgsqlCommand("SELECT nexoruta_app.finish_outbox(@message, @processor, true, NULL)", workerConnection))
        {
            finish.Parameters.AddWithValue("message", jobId);
            finish.Parameters.AddWithValue("processor", processorId);
            try
            {
                Assert.True((bool)(await finish.ExecuteScalarAsync())!);
            }
            catch (PostgresException exception)
            {
                throw new Xunit.Sdk.XunitException($"{exception.MessageText}; Where={exception.Where}; Detail={exception.Detail}; Hint={exception.Hint}; Routine={exception.Routine}");
            }
        }
    }

    [Fact]
    public async Task ConcurrentRestrictedWorkersClaimAQueueMessageOnlyOnce()
    {
        var database = await postgres.CreateDatabaseAsync();
        var rows = await SeedTenantGraphAsync(database);
        var message = OutboxMessage.ForShipment(rows.OperatorB, rows.ShipmentA2,
            OutboxMessageTypes.TrackingRequested, OutboxDestino.RabbitMq, "{}");
        await using (var seed = database.CreateDbContext())
        {
            seed.OutboxMessages.Add(message);
            await seed.SaveChangesAsync();
        }

        var first = await CreateRestrictedConnectionAsync(database, "nexoruta_worker");
        var second = await CreateRestrictedConnectionAsync(database, "nexoruta_worker");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<Guid?> ClaimAsync(RestrictedConnection restricted)
        {
            await using var connection = restricted.Connection;
            await connection.OpenAsync();
            await start.Task;
            await using var command = new NpgsqlCommand("SELECT \"Id\" FROM nexoruta_app.claim_outbox(@processor, 'RabbitMq')", connection);
            command.Parameters.AddWithValue("processor", Guid.NewGuid());
            var result = await command.ExecuteScalarAsync();
            return result is Guid id ? id : null;
        }

        var firstClaim = ClaimAsync(first);
        var secondClaim = ClaimAsync(second);
        start.SetResult();
        var claims = await Task.WhenAll(firstClaim, secondClaim);
        Assert.Single(claims, id => id == message.Id);
        Assert.Single(claims, id => id is null);
    }

    [Fact]
    public async Task RecoveryPairUsesNarrowApiFunctionAndWorkersHaveNoGeneralOutboxRead()
    {
        var database = await postgres.CreateDatabaseAsync();
        var rows = await SeedTenantGraphAsync(database);
        var emailId = Guid.CreateVersion7();
        var eventId = Guid.CreateVersion7();
        var occurredAt = DateTimeOffset.UtcNow;
        var eventPayload = JsonSerializer.Serialize(
            new PasswordRecoveryRequestedV1(eventId, rows.UserA, occurredAt), PasswordRecoveryJson.Options);
        var emailPayload = JsonSerializer.Serialize(new { protectedCommand = "opaque-protected-command" }, PasswordRecoveryJson.Options);

        var api = await CreateRestrictedConnectionAsync(database, "nexoruta_api");
        await using (var connection = api.Connection)
        {
            await connection.OpenAsync();
            await using var enqueue = new NpgsqlCommand(
                "SELECT nexoruta_app.enqueue_password_recovery(@user, @email, @event, @email_payload, @event_payload)", connection);
            enqueue.Parameters.AddWithValue("user", rows.UserA);
            enqueue.Parameters.AddWithValue("email", emailId);
            enqueue.Parameters.AddWithValue("event", eventId);
            enqueue.Parameters.AddWithValue("email_payload", NpgsqlTypes.NpgsqlDbType.Jsonb, emailPayload);
            enqueue.Parameters.AddWithValue("event_payload", NpgsqlTypes.NpgsqlDbType.Jsonb, eventPayload);
            Assert.True((bool)(await enqueue.ExecuteScalarAsync())!);

            await using var read = new NpgsqlCommand("SELECT count(*) FROM \"OutboxMessages\"", connection);
            var deniedRead = await Assert.ThrowsAsync<PostgresException>(() => read.ExecuteScalarAsync());
            Assert.Equal("42501", deniedRead.SqlState);
        }

        var worker = await CreateRestrictedConnectionAsync(database, "nexoruta_worker");
        await using var workerConnection = worker.Connection;
        await workerConnection.OpenAsync();
        await using (var directRead = new NpgsqlCommand("SELECT count(*) FROM \"OutboxMessages\"", workerConnection))
            Assert.Equal(0L, (long)(await directRead.ExecuteScalarAsync())!);

        var processor = Guid.CreateVersion7();
        foreach (var destination in new[] { "Kafka", "RabbitMq" })
        {
            await using var claim = new NpgsqlCommand("SELECT \"Id\", \"Tipo\", \"Ambito\", \"ClaveAmbito\", \"Payload\" FROM nexoruta_app.claim_outbox(@processor, @destination)", workerConnection);
            claim.Parameters.AddWithValue("processor", processor);
            claim.Parameters.AddWithValue("destination", destination);
            await using var reader = await claim.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            var type = reader.GetString(reader.GetOrdinal("Tipo"));
            Assert.Equal(destination == "Kafka" ? OutboxMessageTypes.PasswordRecoveryRequested : OutboxMessageTypes.PasswordRecoveryEmail, type);
            Assert.Equal(nameof(OutboxScope.Identidad), reader.GetString(reader.GetOrdinal("Ambito")));
            Assert.Equal(rows.UserA, reader.GetGuid(reader.GetOrdinal("ClaveAmbito")));
            var id = reader.GetGuid(reader.GetOrdinal("Id"));
            await reader.DisposeAsync();
            await using var finish = new NpgsqlCommand("SELECT nexoruta_app.finish_outbox(@message, @processor, true, NULL)", workerConnection);
            finish.Parameters.AddWithValue("message", id);
            finish.Parameters.AddWithValue("processor", processor);
            Assert.True((bool)(await finish.ExecuteScalarAsync())!);
        }

        await using var verify = database.CreateDbContext();
        Assert.Equal(2, await verify.OutboxMessages.CountAsync());
        Assert.All(await verify.OutboxMessages.ToListAsync(), message =>
        {
            Assert.Null(message.OperadorId);
            Assert.Null(message.EnvioId);
            Assert.Equal(rows.UserA, message.ClaveAmbito);
        });
    }

    [Fact]
    public async Task RestrictedWorkerProcessesConcurrentDuplicateDeliveryIdempotently()
    {
        var database = await postgres.CreateDatabaseAsync();
        var rows = await SeedTenantGraphAsync(database);
        var message = OutboxMessage.ForShipment(rows.OperatorA, rows.ShipmentA1,
            OutboxMessageTypes.TrackingRequested, OutboxDestino.RabbitMq, "{}");
        await using (var seed = database.CreateDbContext())
        {
            seed.OutboxMessages.Add(message);
            await seed.SaveChangesAsync();
        }

        var claimWorker = await CreateRestrictedConnectionAsync(database, "nexoruta_worker");
        await using (var connection = claimWorker.Connection)
        {
            await connection.OpenAsync();
            var processorId = Guid.NewGuid();
            await using (var claim = new NpgsqlCommand("SELECT \"Id\" FROM nexoruta_app.claim_outbox(@processor, 'RabbitMq')", connection))
            {
                claim.Parameters.AddWithValue("processor", processorId);
                Assert.Equal(message.Id, (Guid)(await claim.ExecuteScalarAsync())!);
            }
            await using var finish = new NpgsqlCommand("SELECT nexoruta_app.finish_outbox(@message, @processor, true, NULL)", connection);
            finish.Parameters.AddWithValue("message", message.Id);
            finish.Parameters.AddWithValue("processor", processorId);
            Assert.True((bool)(await finish.ExecuteScalarAsync())!);
        }

        var firstWorker = await CreateRestrictedConnectionAsync(database, "nexoruta_worker");
        var secondWorker = await CreateRestrictedConnectionAsync(database, "nexoruta_worker");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Seguimiento:EncryptionKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        }).Build();
        async Task ProcessAsync(RestrictedConnection worker)
        {
            var options = new DbContextOptionsBuilder<NexoRutaDbContext>()
                .UseNexoRutaPostgres(worker.ConnectionString).Options;
            await using var db = new NexoRutaDbContext(options);
            var createLink = new CrearEnlaceSeguimientoUseCase(
                new EfConsultaEstadoEnvio(db), new EfEnlacesSeguimientoRepository(db));
            var processor = new ProcesarGeneracionSeguimiento(
                db, createLink, new CifradorTokenSeguimiento(configuration));
            await processor.EjecutarAsync(message.Id, rows.OperatorA, rows.ShipmentA1);
        }

        await Task.WhenAll(ProcessAsync(firstWorker), ProcessAsync(secondWorker));
        await using var verify = database.CreateDbContext();
        Assert.Single(await verify.ProcessedMessages.Where(x => x.MessageId == message.Id).ToListAsync());
        Assert.Single(await verify.EntregasSeguimiento.Where(x => x.EnvioId == rows.ShipmentA1).ToListAsync());
        Assert.Single(await verify.EnlacesSeguimiento.Where(x => x.EnvioId == rows.ShipmentA1).ToListAsync());
    }

    [Fact]
    public async Task UpgradeFromThePreviousSchemaPreservesUserAccessAndShipmentGuids()
    {
        var database = await postgres.CreateDatabaseAsync();
        var rows = await SeedTenantGraphAsync(database);
        await using (var db = database.CreateDbContext())
        {
            await db.Database.MigrateAsync(PreviousMigration);
            await HistoricRecipientEmail.MigrateAsync(db);
        }

        await using var verify = new NpgsqlConnection(database.ConnectionString);
        await verify.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT e."Id", e."ComercioId", e."OperadorId", e."CreadoPorUsuarioId", e."DestinatarioId", e."DireccionId",
                   a."Id", a."UsuarioId", a."ComercioId", u."VersionCredencial"
            FROM "Envios" e JOIN "AccesosUsuario" a ON a."UsuarioId" = e."CreadoPorUsuarioId"
            JOIN "Usuarios" u ON u."Id" = e."CreadoPorUsuarioId"
            WHERE e."Id" = @shipment AND a."Id" = @access
            """, verify);
        command.Parameters.AddWithValue("shipment", rows.ShipmentA1);
        command.Parameters.AddWithValue("access", rows.AccessA);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(rows.ShipmentA1, reader.GetGuid(0));
        Assert.Equal(rows.CommerceA, reader.GetGuid(1));
        Assert.Equal(rows.OperatorA, reader.GetGuid(2));
        Assert.Equal(rows.UserA, reader.GetGuid(3));
        Assert.Equal(rows.RecipientA1, reader.GetGuid(4));
        Assert.Equal(rows.AddressA1, reader.GetGuid(5));
        Assert.Equal(rows.AccessA, reader.GetGuid(6));
        Assert.Equal(rows.UserA, reader.GetGuid(7));
        Assert.Equal(rows.CommerceA, reader.GetGuid(8));
        Assert.Equal(0L, reader.GetInt64(9));
    }

    [Fact]
    public async Task DownMigrationRemovesPoliciesAndRestoresPreviousTableOwnership()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using (var db = database.CreateDbContext())
            await db.Database.MigrateAsync(PreviousMigration);

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var verify = new NpgsqlCommand("""
            SELECT count(*) FILTER (WHERE c.relrowsecurity),
                   count(*) FILTER (WHERE c.relowner <> (SELECT oid FROM pg_roles WHERE rolname = current_user)),
                   (SELECT count(*) FROM pg_policies WHERE schemaname = 'public' AND tablename = ANY(@tables)),
                   to_regnamespace('nexoruta_app') IS NULL
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relname = ANY(@tables) AND c.relkind = 'r'
            """, connection);
        verify.Parameters.AddWithValue("tables", ProtectedTables);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0, reader.GetInt64(0));
        Assert.Equal(0, reader.GetInt64(1));
        Assert.Equal(0L, reader.GetInt64(2));
        Assert.True(reader.GetBoolean(3));
    }

    private static async Task<TenantRows> SeedTenantGraphAsync(IntegrationDatabase database)
    {
        var ids = new TenantRows(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using var db = database.CreateDbContext();
        var userA = new Usuario($"a-{Guid.NewGuid():N}@example.test");
        var userB = new Usuario($"b-{Guid.NewGuid():N}@example.test");
        var commerceRole = new Rol("admin", "Admin", null, ids.CommerceA);
        var otherRole = new Rol("admin", "Admin", null, ids.CommerceB);
        var commerceAccess = new AccesoUsuario(userA.Id, null, ids.CommerceA, commerceRole);
        var otherAccess = new AccesoUsuario(userB.Id, null, ids.CommerceB, otherRole);
        db.AddRange(new Comercio("Commerce A", ids.CommerceA), new Comercio("Commerce B", ids.CommerceB),
            new Operador("Operator A", ids.OperatorA), new Operador("Operator B", ids.OperatorB), userA, userB,
            commerceRole, otherRole, commerceAccess, otherAccess);
        await db.SaveChangesAsync();
        await ReglasEnvioTestData.PublicarInicialAsync(db, ids.OperatorA, userA.Id);
        await ReglasEnvioTestData.PublicarInicialAsync(db, ids.OperatorB, userB.Id);
        var a1 = Shipment(ids.OperatorA, ids.CommerceA, userA.Id);
        var a2 = Shipment(ids.OperatorB, ids.CommerceA, userA.Id);
        var b1 = Shipment(ids.OperatorB, ids.CommerceB, userB.Id);
        db.AddRange(a1.Recipient, a1.Address, a1.Shipment, a1.Parcel, a2.Recipient, a2.Address, a2.Shipment, a2.Parcel,
            b1.Recipient, b1.Address, b1.Shipment, b1.Parcel);
        await db.SaveChangesAsync();
        return ids with
        {
            UserA = userA.Id,
            UserB = userB.Id,
            AccessA = commerceAccess.Id,
            AccessB = otherAccess.Id,
            ShipmentA1 = a1.Shipment.Id,
            ShipmentA2 = a2.Shipment.Id,
            ShipmentB1 = b1.Shipment.Id,
            RecipientA1 = a1.Recipient.Id,
            AddressA1 = a1.Address.Id,
            ParcelA1 = a1.Parcel.Id,
            RecipientA2 = a2.Recipient.Id,
            AddressA2 = a2.Address.Id,
            ParcelA2 = a2.Parcel.Id
        };
    }

    private static async Task ExecuteSqlAsync(IntegrationDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static (Destinatario Recipient, Direccion Address, Envio Shipment, Bulto Parcel) Shipment(
        Guid operatorId, Guid commerceId, Guid userId)
    {
        var recipient = new Destinatario(operatorId, "Recipient", "destinatario@example.test");
        var address = new Direccion(operatorId, "Address");
        var shipment = new Envio(operatorId, commerceId, userId, recipient.Id, address.Id, operatorId);
        var parcel = new Bulto(operatorId, shipment.Id, "PKG", 1, 1, 1, 1);
        return (recipient, address, shipment, parcel);
    }

    private static async Task<RestrictedConnection> CreateRestrictedConnectionAsync(IntegrationDatabase database, string memberRole)
    {
        var role = $"rls_{Guid.NewGuid():N}";
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using (var owner = new NpgsqlConnection(database.ConnectionString))
        {
            await owner.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE ROLE {role} LOGIN PASSWORD '{password}' NOSUPERUSER NOBYPASSRLS; GRANT {memberRole} TO {role};", owner);
            await create.ExecuteNonQueryAsync();
        }
        var connectionString = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Username = role, Password = password, Pooling = true, MaxPoolSize = 4
        }.ConnectionString;
        return new(new NpgsqlConnection(connectionString), connectionString);
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string table)
    {
        Assert.Contains(table, ProtectedTables);
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM \"{table}\"", connection, transaction);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> ScalarCountAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, Guid id)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("role_id", id);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task SetContextAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string key, string value)
    {
        await using var command = new NpgsqlCommand("SELECT set_config(@name, @value, true)", connection, transaction);
        command.Parameters.AddWithValue("name", $"nexoruta.{key}");
        command.Parameters.AddWithValue("value", value);
        await command.ExecuteScalarAsync();
    }

    private static async Task AssertDeniedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using (var savepoint = new NpgsqlCommand("SAVEPOINT denied_operation", connection, transaction))
            await savepoint.ExecuteNonQueryAsync();
        PostgresException error;
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
            error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        await using (var rollback = new NpgsqlCommand("ROLLBACK TO SAVEPOINT denied_operation; RELEASE SAVEPOINT denied_operation", connection, transaction))
            await rollback.ExecuteNonQueryAsync();
    }

    private sealed record RestrictedConnection(NpgsqlConnection Connection, string ConnectionString);
    private sealed record TenantRows(Guid CommerceA, Guid CommerceB, Guid OperatorA, Guid OperatorB,
        Guid UserA, Guid UserB, Guid AccessA, Guid AccessB, Guid ShipmentA1, Guid ShipmentA2, Guid ShipmentB1,
        Guid RecipientA1, Guid AddressA1, Guid ParcelA1)
    {
        public Guid RecipientA2 { get; init; } = Guid.NewGuid();
        public Guid AddressA2 { get; init; } = Guid.NewGuid();
        public Guid ParcelA2 { get; init; } = Guid.NewGuid();
    }
}
