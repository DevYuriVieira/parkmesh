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

    public async Task<IReadOnlyList<ActiveLeaseSnapshot>> GetActiveLeasesAsync(
        IReadOnlyCollection<string> bayIds,
        CancellationToken ct = default)
    {
        if (bayIds.Count == 0)
            return Array.Empty<ActiveLeaseSnapshot>();

        var db = _redis.GetDatabase();
        var batch = db.CreateBatch();
        var bayList = bayIds as string[] ?? bayIds.ToArray();
        var valueTasks = new Task<RedisValue>[bayList.Length];
        var ttlTasks = new Task<TimeSpan?>[bayList.Length];

        for (var i = 0; i < bayList.Length; i++)
        {
            var leaseKey = BuildLeaseKey(bayList[i]);
            valueTasks[i] = batch.StringGetAsync(leaseKey);
            ttlTasks[i] = batch.KeyTimeToLiveAsync(leaseKey);
        }

        batch.Execute();
        var values = await Task.WhenAll(valueTasks);
        var ttls = await Task.WhenAll(ttlTasks);
        var active = new List<ActiveLeaseSnapshot>();

        for (var i = 0; i < bayList.Length; i++)
        {
            var rawValue = values[i];
            if (rawValue.IsNullOrEmpty)
                continue;

            var raw = rawValue.ToString();
            var separatorIndex = raw.LastIndexOf(':');
            if (separatorIndex <= 0 ||
                !long.TryParse(raw.AsSpan(separatorIndex + 1), out var fencingToken))
            {
                continue;
            }

            var remainingTtl = ttls[i];
            if (remainingTtl is { } ttl && ttl <= TimeSpan.Zero)
                continue;

            active.Add(new ActiveLeaseSnapshot(
                bayList[i],
                raw[..separatorIndex],
                fencingToken,
                remainingTtl));
        }

        return active;
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            var latency = await _redis.GetDatabase().PingAsync();
            return latency >= TimeSpan.Zero;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis connectivity ping failed.");
            return false;
        }
    }

    private RedisKey BuildLeaseKey(string bayId) => $"{_options.KeyPrefix}:lease:bay:{bayId}";
    private RedisKey BuildFenceKey(string bayId) => $"{_options.KeyPrefix}:fence:bay:{bayId}";
}
