using Microsoft.Extensions.Logging.Abstractions;
using ParkMesh.Application.Abstractions;
using ParkMesh.Application.Allocation;
using ParkMesh.Application.Audit;
using ParkMesh.Application.Routing;
using ParkMesh.Domain;
using ParkMesh.Infrastructure.Audit;
using ParkMesh.Infrastructure.Redis;

namespace ParkMesh.UnitTests;

public sealed class StateHydrationTests
{
    [Fact]
    public async Task RehydrateStateAsync_RestoresActiveLeasesOccupiedBaysOutOfServiceAndCongestionAcrossRestart()
    {
        var sharedLeaseManager = new InMemoryBayLeaseManager();
        var sharedAuditStore = new InMemoryParkingAuditStore();
        await sharedAuditStore.InitializeAsync();
        var auditSink = new AuditChannelSink();

        var graphBeforeRestart = StandardTopologyFactory.CreateStandardGarage();
        var serviceBeforeRestart = new ParkingAllocationService(
            graphBeforeRestart,
            new AStarPathfinder(),
            sharedLeaseManager,
            new InMemoryEdgeCongestionTracker(),
            NullLogger<ParkingAllocationService>.Instance,
            auditSink: auditSink,
            auditStore: sharedAuditStore);

        var leasedAlloc = await serviceBeforeRestart.AllocateAsync(
            new AllocateSpotRequest("GATE-N1", "CAR-LIVE", VehicleType.Standard, TimeSpan.FromMinutes(5)));
        var occupiedAlloc = await serviceBeforeRestart.AllocateAsync(
            new AllocateSpotRequest("GATE-S1", "CAR-PARKED", VehicleType.EV, TimeSpan.FromMinutes(5)));

        Assert.True(leasedAlloc.Succeeded);
        Assert.True(occupiedAlloc.Succeeded);

        var leasedBayId = leasedAlloc.Session!.AssignedBayId;
        var occupiedBayId = occupiedAlloc.Session!.AssignedBayId;

        await serviceBeforeRestart.ReconcilePhysicalOccupancyAsync(occupiedBayId, occupied: true, "CAR-PARKED");

        var maintenanceBayId = graphBeforeRestart.BaysById.Values
            .First(b => b.Status == BayStatus.Free)
            .BayId;

        var drained = new List<ParkingAuditEvent>();
        while (auditSink.TryRead(out var ev))
            drained.Add(ev);

        drained.Add(new ParkingAuditEvent(
            DateTimeOffset.UtcNow,
            AuditEventType.BayOutOfService,
            maintenanceBayId,
            null,
            null,
            null,
            null,
            "manual maintenance"));

        await sharedAuditStore.RecordBatchAsync(drained);

        // Simulate process restart with fresh in-memory graph, sessions and congestion tracker
        var graphAfterRestart = StandardTopologyFactory.CreateStandardGarage();
        var congestionAfterRestart = new InMemoryEdgeCongestionTracker();
        var serviceAfterRestart = new ParkingAllocationService(
            graphAfterRestart,
            new AStarPathfinder(),
            sharedLeaseManager,
            congestionAfterRestart,
            NullLogger<ParkingAllocationService>.Instance,
            auditStore: sharedAuditStore);

        var restoredCount = await serviceAfterRestart.RehydrateStateAsync();

        Assert.Equal(3, restoredCount);
        Assert.Equal(BayStatus.Leased, graphAfterRestart.BaysById[leasedBayId].Status);
        Assert.Equal("CAR-LIVE", graphAfterRestart.BaysById[leasedBayId].CurrentSessionId);
        Assert.Equal(leasedAlloc.Session.FencingToken, graphAfterRestart.BaysById[leasedBayId].FencingToken);
        Assert.Equal(BayStatus.Occupied, graphAfterRestart.BaysById[occupiedBayId].Status);
        Assert.Equal(BayStatus.OutOfService, graphAfterRestart.BaysById[maintenanceBayId].Status);

        var activeSessions = serviceAfterRestart.GetActiveSessions();
        var recovered = Assert.Single(activeSessions);
        Assert.Equal("CAR-LIVE", recovered.VehicleSessionId);
        Assert.Equal(leasedBayId, recovered.AssignedBayId);
        Assert.NotEmpty(congestionAfterRestart.Snapshot());

        var newAllocation = await serviceAfterRestart.AllocateAsync(
            new AllocateSpotRequest("GATE-N1", "CAR-NEW", VehicleType.Standard));
        Assert.True(newAllocation.Succeeded);
        Assert.NotEqual(leasedBayId, newAllocation.Session!.AssignedBayId);
        Assert.NotEqual(occupiedBayId, newAllocation.Session.AssignedBayId);
        Assert.NotEqual(maintenanceBayId, newAllocation.Session.AssignedBayId);
    }

    [Fact]
    public void StateHydrationTracker_GatesReadinessUntilMarkedHydrated()
    {
        var tracker = new StateHydrationTracker();

        Assert.False(tracker.IsHydrated);
        Assert.Null(tracker.LastError);

        tracker.MarkFailed("postgres warming up");
        Assert.False(tracker.IsHydrated);
        Assert.Equal("postgres warming up", tracker.LastError);

        tracker.MarkHydrated(4);
        Assert.True(tracker.IsHydrated);
        Assert.Equal(4, tracker.RestoredStateCount);
        Assert.Null(tracker.LastError);
    }
}
