using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ParkMesh.Application.Abstractions;
using ParkMesh.Application.Allocation;
using ParkMesh.Application.Routing;
using ParkMesh.Domain;
using ParkMesh.Infrastructure.Redis;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace ParkMesh.IntegrationTests;

public sealed class RedisBayLeaseManagerIntegrationTests : IAsyncLifetime
{
    private readonly RedisContainer _redisContainer = new RedisBuilder("redis:7.4-alpine").Build();
    private IConnectionMultiplexer _connection = null!;

    public async Task InitializeAsync()
    {
        await _redisContainer.StartAsync();
        _connection = await ConnectionMultiplexer.ConnectAsync(_redisContainer.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
        await _redisContainer.DisposeAsync();
    }

    [Fact]
    public async Task TryAcquireAndRelease_EnforcesAtomicLockAndMonotonicFencingTokens()
    {
        var manager = CreateManager($"test-{Guid.NewGuid():N}");

        var first = await manager.TryAcquireLeaseAsync("F0-B21", "CAR-1", TimeSpan.FromSeconds(30));
        Assert.True(first.Acquired);
        Assert.Equal(1, first.FencingToken);

        var secondAttempt = await manager.TryAcquireLeaseAsync("F0-B21", "CAR-2", TimeSpan.FromSeconds(30));
        Assert.False(secondAttempt.Acquired);
        Assert.Equal("CAR-1", await manager.GetActiveLeaseHolderAsync("F0-B21"));

        Assert.False(await manager.TryReleaseLeaseAsync("F0-B21", first.FencingToken + 1));
        Assert.Equal("CAR-1", await manager.GetActiveLeaseHolderAsync("F0-B21"));

        Assert.True(await manager.TryReleaseLeaseAsync("F0-B21", first.FencingToken));
        Assert.Null(await manager.GetActiveLeaseHolderAsync("F0-B21"));

        var reacquired = await manager.TryAcquireLeaseAsync("F0-B21", "CAR-2", TimeSpan.FromSeconds(30));
        Assert.True(reacquired.Acquired);
        Assert.Equal(2, reacquired.FencingToken);

        Assert.False(await manager.TryReleaseLeaseAsync("F0-B21", first.FencingToken));
        Assert.Equal("CAR-2", await manager.GetActiveLeaseHolderAsync("F0-B21"));
    }

    [Fact]
    public async Task TryAcquireLeaseAsync_Under50WayContention_AllowsExactlyOneWinner()
    {
        var manager = CreateManager($"race-{Guid.NewGuid():N}");

        var tasks = Enumerable.Range(1, 50)
            .Select(i => manager.TryAcquireLeaseAsync("F1-B10", $"CAR-{i:D2}", TimeSpan.FromSeconds(30)))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r.Acquired));
        Assert.Equal(1, results.Single(r => r.Acquired).FencingToken);
    }

    [Fact]
    public async Task ParallelAllocationService_WithRealRedis_NeverDoubleBooks()
    {
        var manager = CreateManager($"alloc-{Guid.NewGuid():N}");
        var graph = StandardTopologyFactory.CreateStandardGarage();
        var service = new ParkingAllocationService(
            graph,
            new AStarPathfinder(),
            manager,
            new InMemoryEdgeCongestionTracker(),
            NullLogger<ParkingAllocationService>.Instance);

        var tasks = Enumerable.Range(1, 40)
            .Select(i => service.AllocateAsync(
                new AllocateSpotRequest("GATE-N1", $"CAR-{i:D2}", VehicleType.Standard)))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.True(r.Succeeded, r.FailureReason));
        var bayIds = results.Select(r => r.Session!.AssignedBayId).ToList();
        Assert.Equal(40, bayIds.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    private RedisBayLeaseManager CreateManager(string prefix) =>
        new(
            _connection,
            Options.Create(new RedisLeaseOptions { KeyPrefix = prefix }),
            NullLogger<RedisBayLeaseManager>.Instance);
}
