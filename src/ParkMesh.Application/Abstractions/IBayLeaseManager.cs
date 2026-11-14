namespace ParkMesh.Application.Abstractions;

public sealed record LeaseAcquireResult(
    bool Acquired,
    long FencingToken,
    DateTimeOffset? ExpiresAtUtc,
    string? FailureReason = null);

public interface IBayLeaseManager
{
    Task<LeaseAcquireResult> TryAcquireLeaseAsync(
        string bayId,
        string vehicleSessionId,
        TimeSpan ttl,
        CancellationToken ct = default);

    Task<bool> TryReleaseLeaseAsync(
        string bayId,
        long fencingToken,
        CancellationToken ct = default);

    Task<bool> InvalidateLeaseAsync(
        string bayId,
        CancellationToken ct = default);

    Task<string?> GetActiveLeaseHolderAsync(
        string bayId,
        CancellationToken ct = default);
}
