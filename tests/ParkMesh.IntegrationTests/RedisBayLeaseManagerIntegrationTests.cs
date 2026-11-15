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

        var activeLeases = await manager.GetActiveLeasesAsync(graph.BaysById.Keys.ToArray());
        Assert.Equal(40, activeLeases.Count);
        Assert.All(activeLeases, lease =>
        {
            Assert.True(lease.FencingToken >= 1);
            Assert.True(lease.RemainingTtl is { } ttl && ttl > TimeSpan.Zero);
        });
    }

    [Fact]
    public async Task MultiTenantContext_IsolatesRedisLeaseKeysAndSupportsMultiTenantRehydration()
    {
        var prefix = $"tenant-iso-{Guid.NewGuid():N}";
        var tenantContext = new AsyncLocalTenantContext();
        var manager = new RedisBayLeaseManager(
            _connection,
            Options.Create(new RedisLeaseOptions { KeyPrefix = prefix }),
            NullLogger<RedisBayLeaseManager>.Instance,
            tenantContext);

        // 1. Both tenants can acquire the same bay ID in isolated Redis namespaces without collision
        LeaseAcquireResult leaseAlpha;
        using (tenantContext.BeginScope("tenant-alpha"))
        {
            leaseAlpha = await manager.TryAcquireLeaseAsync("F0-B05", "SHARED-CAR", TimeSpan.FromSeconds(45));
        }

        LeaseAcquireResult leaseBeta;
        using (tenantContext.BeginScope("tenant-beta"))
        {
            leaseBeta = await manager.TryAcquireLeaseAsync("F0-B05", "OTHER-CAR", TimeSpan.FromSeconds(45));
        }

        Assert.True(leaseAlpha.Acquired);
        Assert.True(leaseBeta.Acquired);
        Assert.Equal(1, leaseAlpha.FencingToken);
        Assert.Equal(1, leaseBeta.FencingToken);

        // 2. Tenant-scoped release touches only the calling tenant's Redis key
        using (tenantContext.BeginScope("tenant-beta"))
        {
            Assert.False(await manager.TryReleaseLeaseAsync("F0-B05", 99));
        }

        using (tenantContext.BeginScope("tenant-alpha"))
        {
            Assert.Equal("SHARED-CAR", await manager.GetActiveLeaseHolderAsync("F0-B05"));
        }

        LeaseAcquireResult leaseAlphaSecondBay;
        using (tenantContext.BeginScope("tenant-alpha"))
        {
            leaseAlphaSecondBay = await manager.TryAcquireLeaseAsync("F0-B06", "CAR-ALPHA-2", TimeSpan.FromSeconds(45));
        }
        Assert.True(leaseAlphaSecondBay.Acquired);

        using (tenantContext.BeginScope("tenant-beta"))
        {
            var crossRelease = await manager.TryReleaseLeaseAsync("F0-B06", leaseAlphaSecondBay.FencingToken);
            Assert.False(crossRelease);
        }

        // 3. Multi-tenant rehydration restores overlapping and distinct bay IDs independently per tenant garage
        LeaseAcquireResult leaseBetaOtherBay;
        using (tenantContext.BeginScope("tenant-beta"))
        {
            leaseBetaOtherBay = await manager.TryAcquireLeaseAsync("F1-B08", "CAR-BETA", TimeSpan.FromSeconds(45));
        }
        Assert.True(leaseBetaOtherBay.Acquired);

        var freshGraph = StandardTopologyFactory.CreateStandardGarage();
        var service = new ParkingAllocationService(
            freshGraph,
            new AStarPathfinder(),
            manager,
            new InMemoryEdgeCongestionTracker(),
            NullLogger<ParkingAllocationService>.Instance,
            tenantContext: tenantContext);

        var restored = await service.RehydrateStateAsync(["default", "tenant-alpha", "tenant-beta"]);
        Assert.Equal(4, restored);
        Assert.Equal(2, service.GetActiveSessions("tenant-alpha").Count);
        Assert.Equal(2, service.GetActiveSessions("tenant-beta").Count);
        Assert.Empty(service.GetActiveSessions("default"));

        using (tenantContext.BeginScope("tenant-alpha"))
        {
            Assert.Equal("SHARED-CAR", service.Graph.BaysById["F0-B05"].CurrentSessionId);
            Assert.Equal("CAR-ALPHA-2", service.Graph.BaysById["F0-B06"].CurrentSessionId);
        }

        using (tenantContext.BeginScope("tenant-beta"))
        {
            Assert.Equal("OTHER-CAR", service.Graph.BaysById["F0-B05"].CurrentSessionId);
            Assert.Equal("CAR-BETA", service.Graph.BaysById["F1-B08"].CurrentSessionId);
        }
    }

    private RedisBayLeaseManager CreateManager(string prefix) =>
        new(
            _connection,
            Options.Create(new RedisLeaseOptions { KeyPrefix = prefix }),
            NullLogger<RedisBayLeaseManager>.Instance);
}
