using System.Net;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NexoRuta.IntegrationTests.Fixtures;

namespace NexoRuta.IntegrationTests.Hosting;

public sealed class HealthEndpointTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task KestrelReadinessTurnsUnhealthyAsApplicationStartsDraining()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = new IntegrationApiFactory(database);
        factory.UseKestrel(0);
        using var client = factory.CreateClient();
        using (var ready = await client.GetAsync("/ready")) Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        var lifetime = factory.Services.GetRequiredService<IHostApplicationLifetime>();
        var observed = new TaskCompletionSource<(HttpStatusCode Ready, HttpStatusCode Live)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStopping.Register(() =>
        {
            try
            {
                // Cancellation callbacks run before the host stops Kestrel. Probe over the
                // real listener during that interval, after ApplicationStopping is signalled.
                var statuses = Task.Run(async () =>
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    using var ready = await client.GetAsync("/ready", timeout.Token);
                    using var live = await client.GetAsync("/live", timeout.Token);
                    return (ready.StatusCode, live.StatusCode);
                }).GetAwaiter().GetResult();
                observed.TrySetResult(statuses);
            }
            catch (Exception error) { observed.TrySetException(error); }
        });
        lifetime.StopApplication();
        var result = await observed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, result.Ready);
        Assert.Equal(HttpStatusCode.OK, result.Live);
    }

    [Fact]
    public async Task HealthRoutesAreAnonymousConciseAndHaveNoLegacyAliases()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = new IntegrationApiFactory(database);
        using var client = factory.CreateHttpClient();

        foreach (var path in new[] { "/health", "/ready", "/live" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("Exception", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Host=", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Password", body, StringComparison.OrdinalIgnoreCase);
            if (path == "/live")
                Assert.DoesNotContain("postgres", body, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var removedPath in new[] { "/health/ready", "/health/live" })
        {
            using var response = await client.GetAsync(removedPath);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task SqlFailureKeepsLivenessUpAndFailsReadinessWithinProbeBudget()
    {
        var database = await postgres.CreateDatabaseAsync();
        using var factory = new IntegrationApiFactory(database, settings: new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=127.0.0.1;Port=1;Database=unavailable;Username=test;Password=test;Timeout=1;Command Timeout=1",
            ["DatabaseInitialization:ApplyMigrations"] = "false",
            ["DatabaseInitialization:SeedData"] = "false"
        }, replaceDbContext: false);
        using var client = factory.CreateHttpClient();

        using var live = await client.GetAsync("/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        var started = Stopwatch.GetTimestamp();
        using var ready = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5));
        using var body = JsonDocument.Parse(await ready.Content.ReadAsStringAsync());
        Assert.Equal("unhealthy", body.RootElement.GetProperty("checks").GetProperty("postgres").GetString());
    }

    [Fact]
    public async Task SharedDataProtectionStoreParticipatesInProductionReadiness()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var sharedKeys = await SharedDataProtectionTestStore.CreateAsync(database);
        await SharedRateLimitTestStore.InstallAlwaysAllowingStoreAsync(database);
        var settings = new Dictionary<string, string?>();
        sharedKeys.Configure(settings);
        settings["ConnectionStrings:RateLimits"] = database.ConnectionString;
        using var factory = new IntegrationApiFactory(database, "Production", settings);
        using var client = factory.CreateHttpClient();

        using var ready = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        using var body = JsonDocument.Parse(await ready.Content.ReadAsStringAsync());
        Assert.Equal("healthy", body.RootElement.GetProperty("checks").GetProperty("data-protection").GetString());
        Assert.Equal("healthy", body.RootElement.GetProperty("checks").GetProperty("rate-limits").GetString());
    }

    [Fact]
    public async Task RateLimitStoreFailureKeepsLivenessUpAndFailsReadiness()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var sharedKeys = await SharedDataProtectionTestStore.CreateAsync(database);
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:RateLimits"] = "Host=127.0.0.1;Port=1;Database=unavailable;Username=test;Timeout=1;Command Timeout=1"
        };
        sharedKeys.Configure(settings);
        using var factory = new IntegrationApiFactory(database, "Production", settings);
        using var client = factory.CreateHttpClient();
        using var live = await client.GetAsync("/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        var started = Stopwatch.GetTimestamp();
        using var ready = await client.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5));
        using var body = JsonDocument.Parse(await ready.Content.ReadAsStringAsync());
        Assert.Equal("unhealthy", body.RootElement.GetProperty("checks").GetProperty("rate-limits").GetString());
    }

    [Fact]
    public async Task ProductionRequiresSharedRateLimitStorage()
    {
        var database = await postgres.CreateDatabaseAsync();
        await using var sharedKeys = await SharedDataProtectionTestStore.CreateAsync(database);
        var settings = new Dictionary<string, string?> { ["ConnectionStrings:RateLimits"] = "" };
        sharedKeys.Configure(settings);
        using var factory = new IntegrationApiFactory(database, "Production", settings);
        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateHttpClient());
        Assert.Contains("shared rate-limit storage", error.Message);
    }
}
