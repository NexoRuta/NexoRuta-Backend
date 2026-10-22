using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NexoRuta.Api.Core.Security;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.IntegrationTests.Fixtures;

namespace NexoRuta.IntegrationTests.Seguridad;

public sealed class RlsResponseOrderingProbeTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData("success")]
    [InlineData("commit-failure")]
    [InlineData("downstream-failure")]
    [InlineData("rejected")]
    public async Task ResponseReleaseFollowsTransactionDecision(string mode)
    {
        var database = await postgres.CreateDatabaseAsync();
        using var wire = new MemoryStream();
        var guard = new CommitGuard(wire, mode == "commit-failure");
        await using var db = database.CreateDbContext(guard);
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", "probe") }, "probe"));
        context.Response.Body = wire;
        var identity = new IdentidadSinOrganizacion(Guid.NewGuid(), "probe@example.test");
        context.Items[typeof(IdentidadSinOrganizacion)] = identity;
        var middleware = new RlsTransactionMiddleware(async http =>
        {
            http.Response.StatusCode = mode == "rejected" ? 400 : 200;
            await http.Response.WriteAsync(mode == "rejected" ? "error" : "success");
            Assert.Equal(0, wire.Length);
            if (mode == "downstream-failure") throw new IOException("downstream probe");
        });
        async Task Invoke() => await middleware.InvokeAsync(context, db, null!, null!, new IdentityRepo(identity));
        if (mode is "commit-failure" or "downstream-failure")
        {
            await Assert.ThrowsAsync<IOException>(Invoke);
            Assert.Equal(0, wire.Length);
        }
        else
        {
            await Invoke();
            Assert.Equal(mode == "rejected" ? "error" : "success", System.Text.Encoding.UTF8.GetString(wire.ToArray()));
        }
        Assert.Same(wire, context.Response.Body);
        Assert.Equal(mode is "success" or "commit-failure", guard.CommitAttempted);
        Assert.Equal(mode != "success", guard.RolledBack);
    }
    private sealed class CommitGuard(MemoryStream wire, bool fail) : DbTransactionInterceptor
    {
        public bool CommitAttempted;
        public bool RolledBack;
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            CommitAttempted = true;
            Assert.Equal(0, wire.Length);
            if (fail) throw new IOException("commit probe");
            return ValueTask.FromResult(result);
        }
        public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            RolledBack = true;
            Assert.Equal(0, wire.Length);
            return Task.CompletedTask;
        }
    }
    private sealed class IdentityRepo(IdentidadSinOrganizacion identity) : IRegistroRepository
    {
        public Task<IdentidadSinOrganizacion> RegistrarAsync(string email, string password, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdentidadSinOrganizacion?> ObtenerSinOrganizacionAsync(Guid userId, CancellationToken ct) => Task.FromResult<IdentidadSinOrganizacion?>(identity);
    }
}
