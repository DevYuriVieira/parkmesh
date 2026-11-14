using Microsoft.Extensions.Logging.Abstractions;
using ParkMesh.Application.Abstractions;
using ParkMesh.Application.Allocation;
using ParkMesh.Application.Routing;
using ParkMesh.Application.Telemetry;
using ParkMesh.Domain;
using ParkMesh.Infrastructure.Redis;

namespace ParkMesh.UnitTests;

public sealed class LeaseExpirationReconcilerTests
{
    [Fact]
    public async Task PurgeExpiredSessionsAsync_ReleasesBayCongestionAndRecordsMetrics()
    {
        var graph = StandardTopologyFactory.CreateStandardGarage();
        var pathfinder = new AStarPathfinder();
        var leaseManager = new InMemoryBayLeaseManager();
        var congestion = new InMemoryEdgeCongestionTracker();
        var timeProvider = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
        var sink = new RecordingEventSink();
        using var metrics = new EngineMetricsRecorder();

        var service = new ParkingAllocationService(
            graph,
            pathfinder,
            leaseManager,
            congestion,
            NullLogger<ParkingAllocationService>.Instance,
            eventSink: sink,
            metrics: metrics,
            timeProvider: timeProvider);

        var shortAllocation = await service.AllocateAsync(
            new AllocateSpotRequest("GATE-N1", "SHORT-1", VehicleType.Standard, TimeSpan.FromSeconds(10)));
        var longAllocation = await service.AllocateAsync(
            new AllocateSpotRequest("GATE-S1", "LONG-1", VehicleType.Standard, TimeSpan.FromMinutes(15)));

        Assert.True(shortAllocation.Succeeded);
        Assert.True(longAllocation.Succeeded);

        timeProvider.Advance(TimeSpan.FromSeconds(30));

        var purged = await service.PurgeExpiredSessionsAsync();

        Assert.Single(purged);
        Assert.Equal("SHORT-1", purged[0].VehicleSessionId);
        Assert.Equal(shortAllocation.Session!.AssignedBayId, purged[0].BayId);

        Assert.Equal(BayStatus.Free, graph.BaysById[shortAllocation.Session.AssignedBayId].Status);
        Assert.Equal(BayStatus.Leased, graph.BaysById[longAllocation.Session!.AssignedBayId].Status);
        Assert.Single(service.GetActiveSessions());
        Assert.Contains(shortAllocation.Session.AssignedBayId, sink.FreedBays);

        var snapshot = metrics.BuildSnapshot(
            graph,
            service.GetActiveSessions().Count,
            new TelemetryMetricsSnapshot(0, 0, 0, 0, 0));

        Assert.Equal(1, snapshot.LeasesExpiredTotal);
    }

    private sealed class AdjustableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public AdjustableTimeProvider(DateTimeOffset initialUtc)
        {
            _utcNow = initialUtc;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    private sealed class RecordingEventSink : IParkingEventSink
    {
        public List<string> FreedBays { get; } = [];

        public ValueTask OnBayAllocatedAsync(ActiveVehicleSession session, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask OnPhysicalOccupancyChangedAsync(string bayId, BayStatus newStatus, CancellationToken ct = default)
        {
            if (newStatus == BayStatus.Free)
                FreedBays.Add(bayId);

            return ValueTask.CompletedTask;
        }

        public ValueTask OnRouteReassignedAsync(
            string displacedBayId,
            string displacedSessionId,
            ActiveVehicleSession? newSession,
            CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask OnVehicleMovedAsync(string vehicleSessionId, string currentNodeId, CancellationToken ct = default) =>
            ValueTask.CompletedTask;
    }
}
