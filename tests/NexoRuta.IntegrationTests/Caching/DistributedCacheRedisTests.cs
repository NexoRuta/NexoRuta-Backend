using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexoRuta.Infrastructure.Caching;
using NexoRuta.Infrastructure.Hosting;
using NexoRuta.IntegrationTests.Fixtures;
using StackExchange.Redis;

namespace NexoRuta.IntegrationTests.Caching;

public sealed class DistributedCacheRedisTests
{
    [Fact]
    public async Task RedisAclAllowsTheDistributedCacheProviderAndRejectsAdministrativeCommands()
    {
        await using var redis = await RedisTestServer.CreateAsync();
        const string password = "redis-acl-test-only-" + "x7K2p9V4m6N8q3R5";
        using var administrator = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString + ",allowAdmin=true");
        var server = administrator.GetServer(administrator.GetEndPoints().Single());
        const string cacheUser = "nexoruta-acl-test";
        await server.ExecuteAsync("ACL", "SETUSER", cacheUser, "on", ">" + password,
            "resetkeys", "~nexoruta:acl-test:*", "-@all", "+ping", "+get", "+set", "+del", "+hmget", "+hmset",
            "+unlink", "+expire",
            "+hset", "+pexpire", "+eval", "+evalsha", "+client|setname", "+client|setinfo",
            "+info", "+command|info", "+hello", "+echo");

        var options = ConfigurationOptions.Parse(redis.ConnectionString);
        options.User = cacheUser;
        options.Password = password;
        options.AbortOnConnectFail = false;
        options.AllowAdmin = false;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddStackExchangeRedisCache(configuration => configuration.ConfigurationOptions = options);
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<IDistributedCache>();
        const string key = "nexoruta:acl-test:entry";
        await cache.SetStringAsync(key, "provider-works", new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30)
        });

        Assert.Equal("provider-works", await cache.GetStringAsync(key));
        await cache.RemoveAsync(key);
        Assert.Null(await cache.GetStringAsync(key));

        using var restricted = await ConnectionMultiplexer.ConnectAsync(options);
        await Assert.ThrowsAsync<RedisServerException>(async () =>
            await restricted.GetDatabase().ExecuteAsync("FLUSHDB"));
    }

    [Fact]
    public async Task LruEvictedSnapshotIsReportedAsMiss()
    {
        await using var redis = await RedisTestServer.CreateAsync();
        using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString + ",allowAdmin=true");
        var database = connection.GetDatabase();
        var server = connection.GetServer(connection.GetEndPoints().Single());
        await server.ConfigSetAsync("maxmemory", "8mb");
        await server.ConfigSetAsync("maxmemory-policy", "allkeys-lru");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddStackExchangeRedisCache(options => options.Configuration = redis.ConnectionString);
        using var provider = services.BuildServiceProvider();
        var access = new DistributedCacheAccess(provider.GetRequiredService<IDistributedCache>(),
            new DistributedCacheSettings
            {
                Enabled = true,
                Namespace = $"eviction-{Guid.NewGuid():N}",
                MaxEntryBytes = 4096,
                OperatorBranding = new CacheTtlSettings { TtlSeconds = 900 },
                PublishedTariff = new CacheTtlSettings { TtlSeconds = 21600 }
            }, NullLogger<DistributedCacheAccess>.Instance);
        var key = access.Key(DistributedCacheTelemetry.OperatorBranding, "operator:1:version:1");
        await access.SetAsync(DistributedCacheTelemetry.OperatorBranding, key, "warm", 900,
            CancellationToken.None);
        Assert.True(await database.KeyExistsAsync(key));

        var pressureValue = new byte[1024 * 1024];
        for (var index = 0; index < 24; index++)
            await database.StringSetAsync($"{access.Key("test_pressure", index.ToString())}", pressureValue);

        Assert.False(await database.KeyExistsAsync(key));
        var lookup = await access.GetAsync<string>(DistributedCacheTelemetry.OperatorBranding, key,
            _ => true, CancellationToken.None);
        Assert.Equal(DistributedCacheLookup.Miss, lookup.Lookup);
    }

    [Fact]
    public async Task BothBusinessProfilesUseAbsoluteRedisExpiryAndReturnMissAfterExpiration()
    {
        await using var redis = await RedisTestServer.CreateAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddStackExchangeRedisCache(options => options.Configuration = redis.ConnectionString);
        using var provider = services.BuildServiceProvider();
        var distributedCache = provider.GetRequiredService<IDistributedCache>();
        var access = new DistributedCacheAccess(distributedCache, new DistributedCacheSettings
        {
            Enabled = true,
            Namespace = $"ttl-{Guid.NewGuid():N}",
            MaxEntryBytes = 4096,
            OperatorBranding = new CacheTtlSettings { TtlSeconds = 60 },
            PublishedTariff = new CacheTtlSettings { TtlSeconds = 70 }
        }, NullLogger<DistributedCacheAccess>.Instance);
        using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var database = connection.GetDatabase();
        var brandingKey = access.Key(DistributedCacheTelemetry.OperatorBranding, "operator:1:version:1");
        var tariffKey = access.Key(DistributedCacheTelemetry.PublishedTariff, "operator:1:publication:1");

        await access.SetAsync(DistributedCacheTelemetry.OperatorBranding, brandingKey, "branding", 60,
            CancellationToken.None);
        await access.SetAsync(DistributedCacheTelemetry.PublishedTariff, tariffKey, "tariff", 70,
            CancellationToken.None);

        var brandingTtl = await database.KeyTimeToLiveAsync(brandingKey);
        var tariffTtl = await database.KeyTimeToLiveAsync(tariffKey);
        Assert.NotNull(brandingTtl);
        Assert.NotNull(tariffTtl);
        Assert.InRange(brandingTtl!.Value.TotalSeconds, 53, 60);
        Assert.InRange(tariffTtl!.Value.TotalSeconds, 62, 70);
        Assert.True(tariffTtl.Value > brandingTtl.Value);
        Assert.Equal(DistributedCacheLookup.Hit,
            (await access.GetAsync<string>(DistributedCacheTelemetry.OperatorBranding, brandingKey,
                _ => true, CancellationToken.None)).Lookup);
        Assert.Equal(DistributedCacheLookup.Hit,
            (await access.GetAsync<string>(DistributedCacheTelemetry.PublishedTariff, tariffKey,
                _ => true, CancellationToken.None)).Lookup);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(80);
        while (DateTimeOffset.UtcNow < deadline
            && (await database.KeyExistsAsync(brandingKey) || await database.KeyExistsAsync(tariffKey)))
            await Task.Delay(TimeSpan.FromMilliseconds(250));

        Assert.False(await database.KeyExistsAsync(brandingKey));
        Assert.False(await database.KeyExistsAsync(tariffKey));
        Assert.Equal(DistributedCacheLookup.Miss,
            (await access.GetAsync<string>(DistributedCacheTelemetry.OperatorBranding, brandingKey,
                _ => true, CancellationToken.None)).Lookup);
        Assert.Equal(DistributedCacheLookup.Miss,
            (await access.GetAsync<string>(DistributedCacheTelemetry.PublishedTariff, tariffKey,
                _ => true, CancellationToken.None)).Lookup);
    }
}
