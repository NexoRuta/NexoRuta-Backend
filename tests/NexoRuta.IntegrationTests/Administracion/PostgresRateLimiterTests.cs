using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NexoRuta.Api.Core.Security;
using Npgsql;

namespace NexoRuta.IntegrationTests.Administracion;

public sealed class PostgresRateLimiterTests
{
    [Fact]
    public async Task UnavailableDatabaseRejectsHttpRequestWithoutCallingEndpoint()
    {
        await using var source = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unavailable;Username=unavailable;Pooling=false;Timeout=1");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRateLimiter(options =>
        {
            options.AddPolicy("shared", _ => RateLimitPartition.Get("caller", _ => new PostgresRateLimiter(source, "shared", "caller", 5)));
            options.OnRejected = (context, _) =>
            {
                context.HttpContext.Response.StatusCode = context.Lease.TryGetMetadata(PostgresRateLimiter.BackendUnavailableMetadataName, out var unavailable) && unavailable is true
                    ? StatusCodes.Status503ServiceUnavailable
                    : StatusCodes.Status429TooManyRequests;
                return ValueTask.CompletedTask;
            };
        });
        await using var app = builder.Build();
        app.UseRouting();
        app.UseRateLimiter();
        var endpointCalled = false;
        app.MapGet("/limited", () => { endpointCalled = true; return "accepted"; }).RequireRateLimiting("shared");
        await app.StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/limited");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False(endpointCalled);
    }

    [Fact]
    public async Task CallerCancellationPropagatesRatherThanReturningUnavailableLease()
    {
        await using var source = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unavailable;Username=unavailable;Pooling=false");
        using var limiter = new PostgresRateLimiter(source, "shared", "caller", 5);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => limiter.AcquireAsync(1, cancellation.Token).AsTask());
    }
}
