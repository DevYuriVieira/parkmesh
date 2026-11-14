using System.Collections.Concurrent;
using ParkMesh.Application.Abstractions;

namespace ParkMesh.Infrastructure.Redis;

public sealed class InMemoryBayLeaseManager : IBayLeaseManager
{
    private sealed record LeaseEntry(string SessionId, long FencingToken, DateTimeOffset ExpiresAtUtc);
    private readonly record struct LeaseKey(string TenantId, string BayId);

    private sealed class LeaseKeyComparer : IEqualityComparer<LeaseKey>
    {
        public static readonly LeaseKeyComparer Instance = new();

        public bool Equals(LeaseKey x, LeaseKey y) =>
            string.Equals(x.TenantId, y.TenantId, StringComparison.Ordinal) &&
            string.Equals(x.BayId, y.BayId, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(LeaseKey obj)
        {
            var hc = new HashCode();
            hc.Add(obj.TenantId, StringComparer.Ordinal);
            hc.Add(obj.BayId, StringComparer.OrdinalIgnoreCase);
            return hc.ToHashCode();
        }
    }

    private readonly ConcurrentDictionary<LeaseKey, LeaseEntry> _leases = new(LeaseKeyComparer.Instance);
    private readonly ConcurrentDictionary<LeaseKey, long> _fences = new(LeaseKeyComparer.Instance);
    private readonly Lock _lock = new();
    private readonly ITenantContext? _tenantContext;
    private readonly TimeProvider _timeProvider;

    public InMemoryBayLeaseManager(ITenantContext? tenantContext = null, TimeProvider? timeProvider = null)
    {
        _tenantContext = tenantContext;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private LeaseKey BuildKey(string bayId) =>
        new(_tenantContext?.CurrentTenantId ?? TenantDefaults.DefaultTenantId, bayId);

    public Task<LeaseAcquireResult> TryAcquireLeaseAsync(
        string bayId,
        string vehicleSessionId,
        TimeSpan ttl,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bayId);
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleSessionId);

        var now = _timeProvider.GetUtcNow();
        var key = BuildKey(bayId);

        lock (_lock)
        {
            if (_leases.TryGetValue(key, out var existing))
            {
                if (existing.ExpiresAtUtc > now)
                {
                    return Task.FromResult(new LeaseAcquireResult(false, 0, null, "Bay already has an active lease."));
                }

                _leases.TryRemove(key, out _);
            }

            var nextToken = _fences.AddOrUpdate(key, 1, (_, current) => current + 1);
            var expiresAt = now.Add(ttl);
            _leases[key] = new LeaseEntry(vehicleSessionId, nextToken, expiresAt);

            return Task.FromResult(new LeaseAcquireResult(true, nextToken, expiresAt));
        }
    }

    public Task<bool> TryReleaseLeaseAsync(string bayId, long fencingToken, CancellationToken ct = default)
    {
        var key = BuildKey(bayId);

        lock (_lock)
        {
            if (!_leases.TryGetValue(key, out var existing) || existing.ExpiresAtUtc <= _timeProvider.GetUtcNow())
            {
                _leases.TryRemove(key, out _);
                return Task.FromResult(false);
            }

            if (existing.FencingToken != fencingToken)
                return Task.FromResult(false);

            _leases.TryRemove(key, out _);
            return Task.FromResult(true);
        }
    }

    public Task<bool> InvalidateLeaseAsync(string bayId, CancellationToken ct = default)
    {
        return Task.FromResult(_leases.TryRemove(BuildKey(bayId), out _));
    }

    public Task<string?> GetActiveLeaseHolderAsync(string bayId, CancellationToken ct = default)
    {
        var key = BuildKey(bayId);

        lock (_lock)
        {
            if (_leases.TryGetValue(key, out var existing) && existing.ExpiresAtUtc > _timeProvider.GetUtcNow())
            {
                return Task.FromResult<string?>(existing.SessionId);
            }

            return Task.FromResult<string?>(null);
        }
    }

    public Task<IReadOnlyList<ActiveLeaseSnapshot>> GetActiveLeasesAsync(
        IReadOnlyCollection<string> bayIds,
        CancellationToken ct = default)
    {
        var now = _timeProvider.GetUtcNow();
        var active = new List<ActiveLeaseSnapshot>();

        lock (_lock)
        {
            foreach (var bayId in bayIds)
            {
                var key = BuildKey(bayId);
                if (!_leases.TryGetValue(key, out var existing))
                    continue;

                var remaining = existing.ExpiresAtUtc - now;
                if (remaining <= TimeSpan.Zero)
                {
                    _leases.TryRemove(key, out _);
                    continue;
                }

                active.Add(new ActiveLeaseSnapshot(
                    bayId,
                    existing.SessionId,
                    existing.FencingToken,
                    remaining));
            }
        }

        return Task.FromResult<IReadOnlyList<ActiveLeaseSnapshot>>(active);
    }

    public Task<bool> PingAsync(CancellationToken ct = default) => Task.FromResult(true);
}
