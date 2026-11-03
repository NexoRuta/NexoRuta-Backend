using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace NexoRuta.Infrastructure.Caching;

public sealed class DistributedCacheSettings
{
    public bool Enabled { get; init; }
    public string Namespace { get; init; } = "";
    public int OperationTimeoutMilliseconds { get; init; } = 500;
    public int MaxEntryBytes { get; init; } = 2 * 1024 * 1024;
    public bool RedisTls { get; init; }
    public string? RedisPassword { get; init; }
    public string? RedisCaCertificatesBase64Json { get; init; }
    public CacheTtlSettings OperatorBranding { get; init; } = new() { TtlSeconds = 900 };
    public CacheTtlSettings PublishedTariff { get; init; } = new() { TtlSeconds = 21600 };

    internal IReadOnlyList<X509Certificate2> LoadTrustedCertificates()
    {
        if (!RedisTls || string.IsNullOrWhiteSpace(RedisCaCertificatesBase64Json)) return [];
        var values = JsonSerializer.Deserialize<string[]>(RedisCaCertificatesBase64Json)
            ?? throw new InvalidOperationException("Caching:RedisCaCertificatesBase64Json must be a JSON array.");
        if (values.Length is < 1 or > 4)
            throw new InvalidOperationException("Caching requires between one and four Redis TLS CA certificates.");
        try
        {
            return values.Select(value => X509Certificate2.CreateFromPem(
                System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value)))).ToArray();
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("Caching Redis TLS CA certificates must be valid base64.", exception);
        }
    }

    internal static DistributedCacheSettings Read(IConfiguration configuration)
    {
        var settings = configuration.GetSection("Caching").Get<DistributedCacheSettings>() ?? new();
        if (settings.OperationTimeoutMilliseconds is < 50 or > 5000)
            throw new InvalidOperationException("Caching:OperationTimeoutMilliseconds must be between 50 and 5000.");
        if (settings.MaxEntryBytes is < 1024 or > 4 * 1024 * 1024)
            throw new InvalidOperationException("Caching:MaxEntryBytes must be between 1024 and 4194304.");
        ValidateTtl(settings.OperatorBranding.TtlSeconds, "Caching:OperatorBranding:TtlSeconds");
        ValidateTtl(settings.PublishedTariff.TtlSeconds, "Caching:PublishedTariff:TtlSeconds");
        if (settings.Enabled)
        {
            if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("Redis")))
                throw new InvalidOperationException("Caching:Enabled requires ConnectionStrings:Redis.");
            if (!System.Text.RegularExpressions.Regex.IsMatch(settings.Namespace,
                    "^[a-z0-9][a-z0-9-]{1,47}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                throw new InvalidOperationException("Caching:Namespace must contain 2-48 lowercase letters, digits or hyphens.");
            if (settings.RedisTls && string.IsNullOrWhiteSpace(settings.RedisCaCertificatesBase64Json))
                throw new InvalidOperationException("Caching:RedisTls requires Caching:RedisCaCertificatesBase64Json.");
            if (settings.RedisTls && string.IsNullOrWhiteSpace(settings.RedisPassword))
                throw new InvalidOperationException("Caching:RedisTls requires Caching:RedisPassword for AUTH.");
        }
        return settings;
    }

    private static void ValidateTtl(int value, string key)
    {
        if (value is < 60 or > 604800)
            throw new InvalidOperationException($"{key} must be between 60 and 604800 seconds.");
    }
}

public sealed class CacheTtlSettings
{
    public int TtlSeconds { get; init; }
}
