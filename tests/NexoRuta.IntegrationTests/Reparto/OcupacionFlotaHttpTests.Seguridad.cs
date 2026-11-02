using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.Domain.Administracion;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;
using Npgsql;
using static NexoRuta.IntegrationTests.Fixtures.RepartoHttpData;
namespace NexoRuta.IntegrationTests.Reparto;
public sealed partial class OcupacionFlotaHttpTests
{
    [Fact]
    public async Task FleetTenantIsolationIsEnforcedByHttpAndRlsAndFactsCannotBeMutated()
    {
        var data = await postgres.CreateDatabaseAsync(); using var factory = data.CreateApiFactory(); using var client = factory.CreateHttpClient();
        var p = await Preparar(data, client); var route = await Plan(client, p, FechaOperacion);
        using var dispatch = await client.PostAsJsonAsync($"/api/rutas/{route.Id}/despacho", new { route.Version });
        Assert.Equal(HttpStatusCode.OK, dispatch.StatusCode);
        Guid otherOp; Guid otherAccess;
        await using (var db = data.CreateDbContext())
        {
            var op = new Operador("Isolated fleet operator"); otherOp = op.Id;
            var email = $"fleet-{Guid.NewGuid():N}@example.test";
            var user = new Usuario(email, AuthenticationTestData.Hash(email));
            var role = new Rol(CodigosRolesSistema.AdministradorOperador, "Fleet", op.Id, null);
            db.AddRange(op, user, role);
            foreach (var permission in await db.Permisos.Where(x => x.Codigo == "envios.gestionar" || x.Codigo == "envios.consultar").ToArrayAsync())
                db.Add(new RolPermiso(role.Id, permission.Id));
            await db.SaveChangesAsync(); var access = new AccesoUsuario(user.Id, op.Id, null, role); otherAccess = access.Id;
            db.Add(access); await db.SaveChangesAsync();
        }
        var occupation = Assert.Single(Assert.Single((await client.GetFromJsonAsync<VehiculoFlotaResponse[]>("/api/reparto/flota"))!).Ocupaciones);
        await client.SetBearerForAccessAsync(data, otherAccess);
        using (var customDenied = await client.GetAsync("/api/reparto/flota")) Assert.Equal(HttpStatusCode.Forbidden, customDenied.StatusCode);
        await using (var db = data.CreateDbContext())
        {
            (await db.Roles.SingleAsync(r => r.OperadorId == otherOp)).ProtegerComoRolSistema(); await db.SaveChangesAsync();
        }
        Assert.Empty((await client.GetFromJsonAsync<VehiculoFlotaResponse[]>("/api/reparto/flota"))!);
        using var forbidden = await client.PostAsJsonAsync($"/api/reparto/flota/{p.VehiculoId}/retorno", new ComandoFlotaRequest(Guid.NewGuid(), occupation.Id, 0, "Foreign occupation."));
        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        await using var c = new NpgsqlConnection(runtime.ConnectionString); await c.OpenAsync();
        await using var tx = await c.BeginTransactionAsync();
        await using (var context = new NpgsqlCommand("SELECT set_config('nexoruta.actor_kind','operador',true),set_config('nexoruta.org_id',@op,true)", c, tx))
        { context.Parameters.AddWithValue("op", otherOp.ToString()); await context.ExecuteNonQueryAsync(); }
        foreach (var table in new[] { "OcupacionesRuta", "HechosFlota" })
        {
            await using var count = new NpgsqlCommand($"SELECT count(*) FROM \"{table}\"", c, tx);
            Assert.Equal(0L, await count.ExecuteScalarAsync());
        }
        await tx.RollbackAsync();
        await using var owner = data.CreateDbContext();
        var ex = await Assert.ThrowsAsync<PostgresException>(() => owner.Database.ExecuteSqlRawAsync("UPDATE \"HechosFlota\" SET \"Motivo\"='modified'"));
        Assert.Equal("42501", ex.SqlState);
        Assert.Null((await owner.OcupacionesRuta.SingleAsync()).RetornoEnUtc);
    }
}
