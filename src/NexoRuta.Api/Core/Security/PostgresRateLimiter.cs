using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Npgsql;
using NpgsqlTypes;

namespace NexoRuta.Api.Core.Security;

/// <summary>Shares request limits between API instances using PostgreSQL minute windows.</summary>
public sealed class PostgresRateLimiter : RateLimiter
{
    public const string BackendUnavailableMetadataName = "backend-unavailable";

    private static readonly TimeSpan BackendTimeout = TimeSpan.FromSeconds(3);
    private readonly NpgsqlDataSource dataSource;
    private readonly string key;
    private readonly int permitLimit;
    private bool disposed;

    public PostgresRateLimiter(NpgsqlDataSource dataSource, string policy, string partitionKey, int permitLimit)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(policy);
        ArgumentNullException.ThrowIfNull(partitionKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(permitLimit, 1);

        this.dataSource = dataSource;
        this.permitLimit = permitLimit;
        key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{policy}:{partitionKey}")))
            .ToLowerInvariant();
    }

    public override TimeSpan? IdleDuration => null;

    public override RateLimiterStatistics? GetStatistics() => null;

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        // The middleware retries failed synchronous acquisitions through AcquireAsync.
        // Never block its request thread on database I/O.
        return CreateLease(permitCount == 0);
    }

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (permitCount == 0)
            return CreateLease(true);
        if (permitCount != 1)
            return CreateLease(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(BackendTimeout);
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(timeout.Token);
            await using var command = new NpgsqlCommand("SELECT nexoruta_hosting.take_rate_limit(@key, @limit)", connection)
            {
                CommandTimeout = 3
            };
            command.Parameters.AddWithValue("key", NpgsqlDbType.Text, key);
            command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, permitLimit);
            var acquired = await command.ExecuteScalarAsync(timeout.Token) is true;
            return CreateLease(acquired);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CreateLease(false, backendUnavailable: true);
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return CreateLease(false, backendUnavailable: true);
        }
    }

    protected override void Dispose(bool disposing)
    {
        disposed = true;
        // The application owns the shared data source.
        base.Dispose(disposing);
    }

    private static RateLimitLease CreateLease(bool acquired, bool backendUnavailable = false)
    {
        var now = DateTimeOffset.UtcNow;
        var nextWindow = DateTimeOffset.FromUnixTimeSeconds((now.ToUnixTimeSeconds() / 60 + 1) * 60);
        return new PostgresLease(acquired, nextWindow - now, backendUnavailable);
    }

    private sealed class PostgresLease(bool acquired, TimeSpan retryAfter, bool backendUnavailable) : RateLimitLease
    {
        public override bool IsAcquired => acquired;

        public override IEnumerable<string> MetadataNames => acquired
            ? []
            : [MetadataName.RetryAfter.Name, BackendUnavailableMetadataName];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            if (!acquired && metadataName == MetadataName.RetryAfter.Name)
                metadata = retryAfter;
            else if (!acquired && metadataName == BackendUnavailableMetadataName)
                metadata = backendUnavailable;
            return metadata is not null;
        }
    }
}
