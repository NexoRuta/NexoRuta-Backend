using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Domain.Administracion;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.Infrastructure.Administracion;
using NexoRuta.Infrastructure.Persistence;
using Npgsql;

namespace NexoRuta.IntegrationTests.Seguridad;

public sealed class RlsRestrictedHttpFlowTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task RestrictedCommerceHttpCanCreateShipmentAndSeeItAfterChosenOperatorSuspension()
    {
        var database = await postgres.CreateDatabaseAsync();
        const string commerceEmail = "rls-http-commerce@example.test";
        const string otherCommerceEmail = "rls-http-other-commerce@example.test";
        Guid operatorId;
        Guid commerceAccessId;
        Guid commerceId;
        Guid commerceUserId;
        Guid otherCommerceAccessId;
        Guid otherCommerceId;
        Guid otherCommerceUserId;
        await using (var owner = database.CreateDbContext())
        {
            await AuthenticationTestData.Seeder(owner,
                new AccesoInicial(commerceEmail, "Chosen operator", "Test commerce", "rls-http-operator@example.test"))
                .SeedAsync();
            await ReglasEnvioTestData.SembrarInicialesAsync(owner);
            operatorId = await owner.Operadores.Select(x => x.Id).SingleAsync();
            var access = await owner.AccesosUsuario.SingleAsync(x => x.ComercioId != null);
            commerceAccessId = access.Id;
            commerceId = access.ComercioId!.Value;
            commerceUserId = access.UsuarioId;
            await AuthenticationTestData.Seeder(owner,
                new AccesoInicial(otherCommerceEmail, "Other operator", "Other commerce", "rls-http-other-operator@example.test"))
                .SeedAsync();
            var otherAccess = await owner.AccesosUsuario.SingleAsync(x => x.ComercioId != null && x.ComercioId != commerceId);
            otherCommerceAccessId = otherAccess.Id;
            otherCommerceId = otherAccess.ComercioId!.Value;
            otherCommerceUserId = otherAccess.UsuarioId;
        }

        var runtime = await RestrictApiAsync(database);
        using var api = new IntegrationApiFactory(runtime, settings: ApiSettings());
        using var client = api.CreateHttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, commerceEmail));
        using var created = await client.PostAsJsonAsync("/api/envios", new
        {
            OperadorId = operatorId,
            DestinatarioNombre = "Ada Lovelace", DestinatarioEmail = "destinatario@example.test",
            Direccion = "18 de Julio 123",
            CodigoBulto = "RLS-1",
            PesoGramos = 500m,
            LargoCentimetros = 20m,
            AnchoCentimetros = 10m,
            AltoCentimetros = 5m
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        var shipmentId = createdBody.GetProperty("id").GetGuid();

        using var importedCreated = await client.PostAsJsonAsync("/api/envios", new
        {
            OperadorId = operatorId,
            DestinatarioNombre = "Grace Hopper", DestinatarioEmail = "grace@example.test",
            Direccion = "18 de Julio 456",
            CodigoBulto = "RLS-2",
            PesoGramos = 700m,
            LargoCentimetros = 25m,
            AnchoCentimetros = 12m,
            AltoCentimetros = 6m
        });
        Assert.Equal(HttpStatusCode.Created, importedCreated.StatusCode);
        var importedShipmentId = (await importedCreated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        await using (var owner = database.CreateDbContext())
        {
            // The second row represents the import identity; the first shipment remains an individual shipment without a reference.
            await owner.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "IdentidadesEnvioImportado"
                    ("ComercioId", "ReferenciaExterna", "EnvioId", "OperadorId", "VersionHuella", "Huella")
                VALUES ({commerceId}, {"RLS-EXTERNAL-2"}, {importedShipmentId}, {operatorId}, {1}, {RandomNumberGenerator.GetBytes(32)})
                """);
            Assert.Equal(1, await owner.Database.SqlQueryRaw<int>(
                "SELECT count(*)::int AS \"Value\" FROM public.\"EnvioEventos\" WHERE \"EnvioId\" = {0} AND \"Tipo\" = 'Creacion'",
                shipmentId).SingleAsync());
            Assert.False(await owner.Database.SqlQueryRaw<bool>(
                "SELECT has_table_privilege('nexoruta_api', 'public.\"IdentidadesEnvioImportado\"', 'INSERT') OR has_table_privilege('nexoruta_api', 'public.\"IdentidadesEnvioImportado\"', 'UPDATE') OR has_table_privilege('nexoruta_api', 'public.\"IdentidadesEnvioImportado\"', 'DELETE') AS \"Value\"").SingleAsync());
            Assert.True(await owner.Database.SqlQueryRaw<bool>("""
                SELECT c.relrowsecurity AND c.relforcerowsecurity AS "Value"
                FROM pg_class c WHERE c.oid = 'public."IdentidadesEnvioImportado"'::regclass
                """).SingleAsync());
            Assert.True(await owner.Database.SqlQueryRaw<bool>("""
                SELECT NOT r.rolcanlogin AND NOT r.rolsuper AND NOT r.rolbypassrls AS "Value"
                FROM pg_roles r WHERE r.rolname = 'nexoruta_shipment_fn'
                """).SingleAsync());
            Assert.False(await owner.Database.SqlQueryRaw<bool>(
                "SELECT has_table_privilege('nexoruta_api', 'public.\"EnvioEventos\"', 'INSERT') AS \"Value\"").SingleAsync());
            Assert.False(await owner.Database.SqlQueryRaw<bool>(
                "SELECT has_table_privilege('nexoruta_api', 'public.\"EnvioEventos\"', 'UPDATE') AS \"Value\"").SingleAsync());
            Assert.False(await owner.Database.SqlQueryRaw<bool>(
                "SELECT has_table_privilege('nexoruta_api', 'public.\"EnvioEventos\"', 'DELETE') AS \"Value\"").SingleAsync());
            Assert.True(await owner.Database.SqlQueryRaw<bool>("""
                SELECT NOT r.rolcanlogin AND NOT r.rolinherit AND NOT r.rolsuper AND NOT r.rolcreatedb
                    AND NOT r.rolcreaterole AND NOT r.rolreplication AND NOT r.rolbypassrls
                    AND NOT EXISTS (SELECT 1 FROM pg_auth_members m WHERE m.roleid = r.oid) AS "Value"
                FROM pg_roles r WHERE r.rolname = 'nexoruta_history_fn'
                """).SingleAsync());
            Assert.True(await owner.Database.SqlQueryRaw<bool>("""
                SELECT has_table_privilege('nexoruta_history_fn', 'public."EnvioEventos"', 'INSERT')
                    AND has_table_privilege('nexoruta_history_fn', 'public."EnvioEventos"', 'SELECT')
                    AND NOT has_table_privilege('nexoruta_history_fn', 'public."EnvioEventos"', 'UPDATE')
                    AND NOT has_table_privilege('nexoruta_history_fn', 'public."EnvioEventos"', 'DELETE')
                    AND NOT has_table_privilege('nexoruta_history_fn', 'public."EnvioEventos"', 'TRUNCATE') AS "Value"
                """).SingleAsync());
            Assert.False(await owner.Database.SqlQueryRaw<bool>(
                "SELECT has_function_privilege('nexoruta_api', 'public.capture_shipment_history()', 'EXECUTE') AS \"Value\"").SingleAsync());
            Assert.False(await owner.Database.SqlQueryRaw<bool>("""
                SELECT has_table_privilege('nexoruta_worker', 'public."EnvioEventos"', 'INSERT')
                    OR has_table_privilege('nexoruta_worker', 'public."EnvioEventos"', 'UPDATE')
                    OR has_table_privilege('nexoruta_worker', 'public."EnvioEventos"', 'DELETE') AS "Value"
                """).SingleAsync());
            Assert.True(await owner.Database.SqlQueryRaw<bool>("""
                SELECT c.relrowsecurity AND c.relforcerowsecurity AS "Value"
                FROM pg_class c WHERE c.oid = 'public."EnvioEventos"'::regclass
                """).SingleAsync());
            Assert.True(await owner.Database.SqlQueryRaw<bool>("""
                SELECT count(*) = 2 AND bool_and(p.prosecdef AND r.rolname = 'nexoruta_history_fn'
                    AND p.proconfig @> ARRAY['search_path=pg_catalog, nexoruta_app']) AS "Value"
                FROM pg_proc p JOIN pg_roles r ON r.oid = p.proowner
                WHERE p.proname IN ('capture_shipment_history', 'capture_cancellation_history')
                    AND p.pronamespace = 'public'::regnamespace
                """).SingleAsync());
        }

        await using (var owner = database.CreateDbContext())
        {
            var selectedOperator = await owner.Operadores.SingleAsync(x => x.Id == operatorId);
            selectedOperator.CambiarEstado(EstadoOrganizacion.Suspendida);
            await owner.SaveChangesAsync();
        }

        using var listed = await client.GetAsync("/api/envios");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        await using (var owner = database.CreateDbContext())
        {
            Assert.False(await owner.Database.SqlQueryRaw<bool>(
                "SELECT has_table_privilege('nexoruta_api', 'public.\"IdentidadesEnvioImportado\"', 'SELECT') AS \"Value\"").SingleAsync());
            Assert.True(await owner.Database.SqlQueryRaw<bool>(
                "SELECT has_function_privilege('nexoruta_api', 'nexoruta_app.list_import_shipment_references(uuid[])', 'EXECUTE') AS \"Value\"").SingleAsync());
        }
        var shipments = await listed.Content.ReadFromJsonAsync<JsonElement[]>();
        var individual = Assert.Single(shipments!, x => x.GetProperty("id").GetGuid() == shipmentId);
        Assert.Equal("Ada Lovelace", individual.GetProperty("destinatarioNombre").GetString());
        Assert.Equal("destinatario@example.test", individual.GetProperty("destinatarioEmail").GetString());
        Assert.Null(individual.GetProperty("referenciaExterna").GetString());
        var importedShipment = Assert.Single(shipments!, x => x.GetProperty("id").GetGuid() == importedShipmentId);
        Assert.Equal("RLS-EXTERNAL-2", importedShipment.GetProperty("referenciaExterna").GetString());
        Assert.Equal("Chosen operator", importedShipment.GetProperty("operadorNombre").GetString());

        await using (var otherCommerce = runtime.CreateDbContext())
        await using (var otherTransaction = await otherCommerce.BeginOrJoinAsync())
        {
            await otherCommerce.ApplyAsync(new RlsActorContext(RlsActorKind.Comercio, otherCommerceUserId,
                otherCommerceAccessId, otherCommerceId, OrganizationType: TipoAccesoUsuario.Comercio));
            var foreignReferences = await otherCommerce.Database.SqlQueryRaw<Guid>(
                "SELECT \"EnvioId\" AS \"Value\" FROM nexoruta_app.list_import_shipment_references({0})",
                new object[] { new[] { importedShipmentId } }).ToArrayAsync();
            Assert.Empty(foreignReferences);
            await otherTransaction.CommitAsync();
        }

        await using var restrictedHistory = runtime.CreateDbContext();
        await using var historyTransaction = await restrictedHistory.BeginOrJoinAsync();
        await restrictedHistory.ApplyAsync(new RlsActorContext(RlsActorKind.Comercio, commerceUserId,
            commerceAccessId, commerceId, OrganizationType: TipoAccesoUsuario.Comercio));
        var visibleHistory = Assert.Single(await restrictedHistory.EnvioEventos
            .Where(x => x.EnvioId == shipmentId && x.Tipo == "Creacion").ToArrayAsync());
        await historyTransaction.CommitAsync();

        await using (var immutable = database.CreateDbContext())
        {
            var update = await Assert.ThrowsAsync<PostgresException>(() => immutable.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE public.\"EnvioEventos\" SET \"ActorProceso\" = 'tampered' WHERE \"Id\" = {visibleHistory.Id}"));
            Assert.Equal("55000", update.SqlState);
        }
        await using (var immutable = database.CreateDbContext())
        {
            var delete = await Assert.ThrowsAsync<PostgresException>(() => immutable.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM public.\"EnvioEventos\" WHERE \"Id\" = {visibleHistory.Id}"));
            Assert.Equal("55000", delete.SqlState);
        }
        await using (var immutable = database.CreateDbContext())
        {
            var truncate = await Assert.ThrowsAsync<PostgresException>(() => immutable.Database.ExecuteSqlRawAsync(
                "TRUNCATE public.\"EnvioEventos\""));
            Assert.Equal("55000", truncate.SqlState);
        }
    }

    [Fact]
    public async Task ShipmentHistoryRlsSeparatesCommerceAndOperatorRows()
    {
        var database = await postgres.CreateDatabaseAsync();
        const string commerceAEmail = "history-rls-commerce-a@example.test";
        const string commerceBEmail = "history-rls-commerce-b@example.test";
        Guid operatorA;
        Guid operatorB;
        Guid commerceA;
        Guid commerceB;
        (Guid UserId, Guid AccessId) commerceActorA;
        (Guid UserId, Guid AccessId) commerceActorB;
        (Guid UserId, Guid AccessId) operatorActorA;
        (Guid UserId, Guid AccessId) operatorActorB;
        await using (var owner = database.CreateDbContext())
        {
            await AuthenticationTestData.Seeder(owner,
                new AccesoInicial(commerceAEmail, "History operator A", "History commerce A", "history-operator-a@example.test"))
                .SeedAsync();
            await AuthenticationTestData.Seeder(owner,
                new AccesoInicial(commerceBEmail, "History operator B", "History commerce B", "history-operator-b@example.test"))
                .SeedAsync();
            await ReglasEnvioTestData.SembrarInicialesAsync(owner);

            var commerceUserIdA = await owner.Usuarios.Where(x => x.Email == commerceAEmail).Select(x => x.Id).SingleAsync();
            var commerceUserIdB = await owner.Usuarios.Where(x => x.Email == commerceBEmail).Select(x => x.Id).SingleAsync();
            var operatorUserIdA = await owner.Usuarios.Where(x => x.Email == "history-operator-a@example.test").Select(x => x.Id).SingleAsync();
            var operatorUserIdB = await owner.Usuarios.Where(x => x.Email == "history-operator-b@example.test").Select(x => x.Id).SingleAsync();
            var accessA = await owner.AccesosUsuario.SingleAsync(x => x.ComercioId != null && x.UsuarioId == commerceUserIdA);
            var accessB = await owner.AccesosUsuario.SingleAsync(x => x.ComercioId != null && x.UsuarioId == commerceUserIdB);
            operatorA = (await owner.AccesosUsuario.SingleAsync(x => x.UsuarioId == operatorUserIdA)).OperadorId!.Value;
            operatorB = (await owner.AccesosUsuario.SingleAsync(x => x.UsuarioId == operatorUserIdB)).OperadorId!.Value;
            commerceA = accessA.ComercioId!.Value;
            commerceB = accessB.ComercioId!.Value;
            commerceActorA = (accessA.UsuarioId, accessA.Id);
            commerceActorB = (accessB.UsuarioId, accessB.Id);
            var accessOperatorA = await owner.AccesosUsuario.FirstAsync(x => x.OperadorId == operatorA);
            var accessOperatorB = await owner.AccesosUsuario.FirstAsync(x => x.OperadorId == operatorB);
            operatorActorA = (accessOperatorA.UsuarioId, accessOperatorA.Id);
            operatorActorB = (accessOperatorB.UsuarioId, accessOperatorB.Id);
        }

        var runtime = await RestrictApiAsync(database);
        using var api = new IntegrationApiFactory(runtime, settings: ApiSettings());
        using var commerceClientA = api.CreateHttpClient();
        using var commerceClientB = api.CreateHttpClient();
        commerceClientA.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(commerceClientA, commerceAEmail));
        commerceClientB.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(commerceClientB, commerceBEmail));
        var shipmentA = await CreateShipmentAsync(commerceClientA, operatorA, "HISTORY-RLS-A");
        var shipmentB = await CreateShipmentAsync(commerceClientB, operatorB, "HISTORY-RLS-B");

        await using var restricted = runtime.CreateDbContext();
        await using var tx = await restricted.BeginOrJoinAsync();
        await AssertOnlyVisibleAsync(restricted, new RlsActorContext(RlsActorKind.Comercio,
            commerceActorA.UserId, commerceActorA.AccessId, commerceA, OrganizationType: TipoAccesoUsuario.Comercio), shipmentA);
        await AssertOnlyVisibleAsync(restricted, new RlsActorContext(RlsActorKind.Comercio,
            commerceActorB.UserId, commerceActorB.AccessId, commerceB, OrganizationType: TipoAccesoUsuario.Comercio), shipmentB);
        await AssertOnlyVisibleAsync(restricted, new RlsActorContext(RlsActorKind.Operador,
            operatorActorA.UserId, operatorActorA.AccessId, operatorA, OrganizationType: TipoAccesoUsuario.Operador), shipmentA);
        await AssertOnlyVisibleAsync(restricted, new RlsActorContext(RlsActorKind.Operador,
            operatorActorB.UserId, operatorActorB.AccessId, operatorB, OrganizationType: TipoAccesoUsuario.Operador), shipmentB);
        await tx.CommitAsync();
    }

    private static async Task<Guid> CreateShipmentAsync(HttpClient client, Guid operatorId, string code)
    {
        using var response = await client.PostAsJsonAsync("/api/envios", new
        {
            OperadorId = operatorId,
            DestinatarioNombre = "History recipient",
            DestinatarioEmail = "history-recipient@example.test",
            Direccion = "18 de Julio 123",
            CodigoBulto = code,
            PesoGramos = 500m,
            LargoCentimetros = 20m,
            AnchoCentimetros = 10m,
            AltoCentimetros = 5m
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task AssertOnlyVisibleAsync(NexoRutaDbContext db, RlsActorContext actor, Guid shipmentId)
    {
        await db.ApplyAsync(actor);
        Assert.Equal(new[] { shipmentId }, await db.EnvioEventos.Select(x => x.EnvioId).ToArrayAsync());
    }

    [Fact]
    public async Task RestrictedOperatorHttpCannotCreateShipmentOrOutboxRows()
    {
        var database = await postgres.CreateDatabaseAsync();
        const string commerceEmail = "rls-http-deny-commerce@example.test";
        const string operatorEmail = "rls-http-deny-operator@example.test";
        Guid operatorId;
        await using (var owner = database.CreateDbContext())
        {
            await AuthenticationTestData.Seeder(owner,
                new AccesoInicial(commerceEmail, "Chosen operator", "Test commerce", operatorEmail))
                .SeedAsync();
            await ReglasEnvioTestData.SembrarInicialesAsync(owner);
            operatorId = await owner.Operadores.Select(x => x.Id).SingleAsync();
        }

        var runtime = await RestrictApiAsync(database);
        using var api = new IntegrationApiFactory(runtime, settings: ApiSettings());
        using var client = api.CreateHttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, operatorEmail));

        using var response = await client.PostAsJsonAsync("/api/envios", new
        {
            OperadorId = operatorId,
            DestinatarioNombre = "Ada Lovelace", DestinatarioEmail = "destinatario@example.test",
            Direccion = "18 de Julio 123",
            CodigoBulto = "RLS-DENY-1",
            PesoGramos = 500m,
            LargoCentimetros = 20m,
            AnchoCentimetros = 10m,
            AltoCentimetros = 5m
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var verify = database.CreateDbContext();
        Assert.Empty(await verify.Envios.ToArrayAsync());
        Assert.Empty(await verify.Destinatarios.ToArrayAsync());
        Assert.Empty(await verify.Direcciones.ToArrayAsync());
        Assert.Empty(await verify.Bultos.ToArrayAsync());
        Assert.Empty(await verify.OutboxMessages.ToArrayAsync());
    }

    [Fact]
    public async Task RestrictedCommerceCanRemoveRolePermissionsAndAssignProtectedRole()
    {
        var database = await postgres.CreateDatabaseAsync();
        const string commerceEmail = "rls-http-role-owner@example.test";
        Guid commerceId;
        Guid ownerRoleId;
        Guid employeeAccessId;
        await using (var owner = database.CreateDbContext())
        {
            await AuthenticationTestData.Seeder(owner,
                new AccesoInicial(commerceEmail, "Role test operator", "Role test commerce", "rls-role-operator@example.test"))
                .SeedAsync();
            var access = await owner.AccesosUsuario.SingleAsync(x => x.ComercioId != null);
            commerceId = access.ComercioId!.Value;
            ownerRoleId = access.RolId!.Value;
            var permissionId = await owner.Permisos.Where(x => x.Codigo == "configuracion.gestionar")
                .Select(x => x.Id).SingleAsync();
            if (!await owner.RolesPermisos.AnyAsync(x => x.RolId == ownerRoleId && x.PermisoId == permissionId))
                owner.RolesPermisos.Add(new RolPermiso(ownerRoleId, permissionId));
            var employee = new Usuario("rls-http-employee@example.test", AuthenticationTestData.Hash("rls-http-employee@example.test"));
            var employeeAccess = new AccesoUsuario(employee.Id, null, commerceId);
            owner.AddRange(employee, employeeAccess);
            await owner.SaveChangesAsync();
            employeeAccessId = employeeAccess.Id;
        }

        var runtime = await RestrictApiAsync(database);
        using var api = new IntegrationApiFactory(runtime, settings: ApiSettings());
        using var client = api.CreateHttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await LoginAsync(client, commerceEmail));

        using var createdRole = await client.PostAsJsonAsync("/api/roles", new { Codigo = "rls-editable", Nombre = "RLS editable" });
        Assert.Equal(HttpStatusCode.Created, createdRole.StatusCode);
        var roleId = (await createdRole.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var grants = await client.PutAsJsonAsync($"/api/roles/{roleId}/permisos", new { Permisos = new[] { "envios.consultar" } });
        Assert.Equal(HttpStatusCode.NoContent, grants.StatusCode);
        using var grantedRoles = await client.GetAsync("/api/roles");
        var grantedRole = (await grantedRoles.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()
            .Single(x => x.GetProperty("id").GetGuid() == roleId);
        Assert.Contains("envios.consultar", grantedRole.GetProperty("permisos").EnumerateArray().Select(x => x.GetString()));
        using var removeGrants = await client.PutAsJsonAsync($"/api/roles/{roleId}/permisos", new { Permisos = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.NoContent, removeGrants.StatusCode);
        using var removedRoles = await client.GetAsync("/api/roles");
        var removedRole = (await removedRoles.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()
            .Single(x => x.GetProperty("id").GetGuid() == roleId);
        Assert.Empty(removedRole.GetProperty("permisos").EnumerateArray());
        using var deleteRole = await client.DeleteAsync($"/api/roles/{roleId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteRole.StatusCode);

        using var assignProtectedRole = await client.PutAsJsonAsync(
            $"/api/roles/accesos/{employeeAccessId}", new { RolId = ownerRoleId });
        Assert.Equal(HttpStatusCode.NoContent, assignProtectedRole.StatusCode);
        await using var verify = database.CreateDbContext();
        Assert.Equal(ownerRoleId, (await verify.AccesosUsuario.SingleAsync(x => x.Id == employeeAccessId)).RolId);
        Assert.True(await verify.Roles.AsNoTracking().AnyAsync(x => x.Id == ownerRoleId));
    }

    internal static Dictionary<string, string?> ApiSettings()
    {
        var settings = AuthenticationTestData.Settings();
        settings["DatabaseInitialization:SeedData"] = "false";
        return settings;
    }

    private static async Task<string> LoginAsync(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            Email = email,
            Password = AuthenticationTestData.Password
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    internal static async Task<IntegrationDatabase> RestrictApiAsync(IntegrationDatabase database)
    {
        var role = $"rls_http_{Guid.NewGuid():N}";
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using (var owner = new NpgsqlConnection(database.ConnectionString))
        {
            await owner.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE ROLE {role} LOGIN PASSWORD '{password}' NOSUPERUSER NOBYPASSRLS; GRANT nexoruta_api TO {role};", owner);
            await create.ExecuteNonQueryAsync();
        }
        var connectionString = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Username = role,
            Password = password,
            Pooling = true,
            MaxPoolSize = 4
        }.ConnectionString;
        var restrictedDatabase = new IntegrationDatabase(connectionString);
        return restrictedDatabase;
    }
}
