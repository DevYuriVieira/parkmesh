using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ParkMesh.Application.Abstractions;
using StackExchange.Redis;

namespace ParkMesh.Infrastructure.Redis;

public sealed class RedisLeaseOptions
{
    public const string SectionName = "RedisLease";
    public string KeyPrefix { get; set; } = "parkmesh";
}

public sealed class RedisBayLeaseManager : IBayLeaseManager
{
    private readonly IConnectionMultiplexer _redis;
    private readonly RedisLeaseOptions _options;
    private readonly ILogger<RedisBayLeaseManager> _logger;

    public RedisBayLeaseManager(
        IConnectionMultiplexer redis,
        IOptions<RedisLeaseOptions> options,
        ILogger<RedisBayLeaseManager> logger)
    {
        _redis = redis;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<LeaseAcquireResult> TryAcquireLeaseAsync(
        string bayId,
        string vehicleSessionId,
        TimeSpan ttl,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bayId);
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleSessionId);

        if (ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl), "Lease TTL must be positive.");

        var db = _redis.GetDatabase();
        var ttlMs = Math.Max(1L, (long)Math.Ceiling(ttl.TotalMilliseconds));

        var result = (RedisResult[]?)await db.ScriptEvaluateAsync(
            LuaScripts.AcquireBayLease,
            keys: [BuildLeaseKey(bayId), BuildFenceKey(bayId)],
            values: [vehicleSessionId, ttlMs]);

        if (result is null || result.Length < 2)
        {
            _logger.LogError("Unexpected Redis Lua response acquiring lease for bay {BayId}.", bayId);
            return new LeaseAcquireResult(false, 0, null, "Unexpected Redis Lua response.");
        }

        var acquired = (long)result[0] == 1;
        var fencingToken = (long)result[1];

        if (!acquired)
        {
            return new LeaseAcquireResult(false, 0, null, "Bay already has an active lease.");
        }

        var expiresAt = DateTimeOffset.UtcNow.AddMilliseconds(ttlMs);
        return new LeaseAcquireResult(true, fencingToken, expiresAt);
    }

    public async Task<bool> TryReleaseLeaseAsync(
        string bayId,
        long fencingToken,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bayId);

        var db = _redis.GetDatabase();
        var released = (long)await db.ScriptEvaluateAsync(
            LuaScripts.ReleaseBayLease,
            keys: [BuildLeaseKey(bayId)],
            values: [fencingToken.ToString()]);

        return released == 1;
    }

    public async Task<bool> InvalidateLeaseAsync(string bayId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bayId);

        var db = _redis.GetDatabase();
        return await db.KeyDeleteAsync(BuildLeaseKey(bayId));
    }

    public async Task<string?> GetActiveLeaseHolderAsync(string bayId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bayId);

        var db = _redis.GetDatabase();
        var value = await db.StringGetAsync(BuildLeaseKey(bayId));
        if (value.IsNullOrEmpty)
            return null;

        var raw = value.ToString();
        var separatorIndex = raw.LastIndexOf(':');
        return separatorIndex > 0 ? raw[..separatorIndex] : raw;
    }

    private RedisKey BuildLeaseKey(string bayId) => $"{_options.KeyPrefix}:lease:bay:{bayId}";
    private RedisKey BuildFenceKey(string bayId) => $"{_options.KeyPrefix}:fence:bay:{bayId}";
}
