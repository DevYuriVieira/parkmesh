using System.Collections.Concurrent;
using ParkMesh.Application.Abstractions;

namespace ParkMesh.Infrastructure.Redis;

public sealed class InMemoryBayLeaseManager : IBayLeaseManager
{
    private sealed record LeaseEntry(string SessionId, long FencingToken, DateTimeOffset ExpiresAtUtc);

    private readonly ConcurrentDictionary<string, LeaseEntry> _leases = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _fences = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public Task<LeaseAcquireResult> TryAcquireLeaseAsync(
        string bayId,
        string vehicleSessionId,
        TimeSpan ttl,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bayId);
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleSessionId);

        var now = DateTimeOffset.UtcNow;

        lock (_lock)
        {
            if (_leases.TryGetValue(bayId, out var existing))
            {
                if (existing.ExpiresAtUtc > now)
                {
                    return Task.FromResult(new LeaseAcquireResult(false, 0, null, "Bay already has an active lease."));
                }

                _leases.TryRemove(bayId, out _);
            }

            var nextToken = _fences.AddOrUpdate(bayId, 1, (_, current) => current + 1);
            var expiresAt = now.Add(ttl);
            _leases[bayId] = new LeaseEntry(vehicleSessionId, nextToken, expiresAt);

            return Task.FromResult(new LeaseAcquireResult(true, nextToken, expiresAt));
        }
    }

    public Task<bool> TryReleaseLeaseAsync(string bayId, long fencingToken, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_leases.TryGetValue(bayId, out var existing) || existing.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                _leases.TryRemove(bayId, out _);
                return Task.FromResult(false);
            }

            if (existing.FencingToken != fencingToken)
                return Task.FromResult(false);

            _leases.TryRemove(bayId, out _);
            return Task.FromResult(true);
        }
    }

    public Task<bool> InvalidateLeaseAsync(string bayId, CancellationToken ct = default)
    {
        return Task.FromResult(_leases.TryRemove(bayId, out _));
    }

    public Task<string?> GetActiveLeaseHolderAsync(string bayId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_leases.TryGetValue(bayId, out var existing) && existing.ExpiresAtUtc > DateTimeOffset.UtcNow)
            {
                return Task.FromResult<string?>(existing.SessionId);
            }

            return Task.FromResult<string?>(null);
        }
    }

    public Task<bool> PingAsync(CancellationToken ct = default) => Task.FromResult(true);
}
