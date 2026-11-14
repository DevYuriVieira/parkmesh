using Microsoft.Extensions.Logging.Abstractions;
using ParkMesh.Application.Abstractions;
using ParkMesh.Application.Allocation;
using ParkMesh.Application.Audit;
using ParkMesh.Application.Routing;
using ParkMesh.Application.Telemetry;
using ParkMesh.Domain;
using ParkMesh.Infrastructure.Redis;

namespace ParkMesh.UnitTests;

public sealed class MaintenanceControlTests
{
    [Fact]
    public async Task SetBayMaintenanceAsync_MarksLeasedBayOutOfServiceAndReroutesVehicle()
    {
        var graph = StandardTopologyFactory.CreateStandardGarage();
        var leaseManager = new InMemoryBayLeaseManager();
        var congestion = new InMemoryEdgeCongestionTracker();
        var sink = new CapturingEventSink();
        var auditSink = new AuditChannelSink();
        using var metrics = new EngineMetricsRecorder();

        var service = new ParkingAllocationService(
            graph,
            new AStarPathfinder(),
            leaseManager,
            congestion,
            NullLogger<ParkingAllocationService>.Instance,
            eventSink: sink,
            metrics: metrics,
            auditSink: auditSink);

        var initial = await service.AllocateAsync(
            new AllocateSpotRequest("GATE-N1", "CAR-MAINT-1", VehicleType.Standard));
        Assert.True(initial.Succeeded);

        var originalBayId = initial.Session!.AssignedBayId;
        var midNode = initial.Session.Route.NodeIds.Count > 1
            ? initial.Session.Route.NodeIds[1]
            : initial.Session.LastKnownNodeId;
        Assert.True(service.UpdateVehicleProgress("CAR-MAINT-1", midNode));

        var result = await service.SetBayMaintenanceAsync(originalBayId, outOfService: true);

        Assert.Equal(originalBayId, result.BayId);
        Assert.Equal(BayStatus.OutOfService, result.NewStatus);
        Assert.False(result.WasIntruder);
        Assert.Equal("CAR-MAINT-1", result.DisplacedSessionId);
        Assert.NotNull(result.ReroutedSession);
        Assert.NotEqual(originalBayId, result.ReroutedSession!.AssignedBayId);
        Assert.Equal(midNode, result.ReroutedSession.LastKnownNodeId);

        Assert.Equal(BayStatus.OutOfService, graph.BaysById[originalBayId].Status);
        Assert.Null(await leaseManager.GetActiveLeaseHolderAsync(originalBayId));

        Assert.Contains((originalBayId, BayStatus.OutOfService), sink.BayChanges);
        Assert.Single(sink.Reassignments);
        Assert.Equal(originalBayId, sink.Reassignments[0].DisplacedBayId);
        Assert.Equal("CAR-MAINT-1", sink.Reassignments[0].DisplacedSessionId);
        Assert.Equal(result.ReroutedSession.AssignedBayId, sink.Reassignments[0].NewBayId);

        var events = new List<ParkingAuditEvent>();
        while (auditSink.TryRead(out var ev))
            events.Add(ev);

        Assert.Contains(events, e => e.EventType == AuditEventType.BayOutOfService && e.BayId == originalBayId);
        Assert.Equal(1, metrics.BuildSnapshot(graph, 1, new TelemetryMetricsSnapshot(0, 0, 0, 0, 0)).OutOfServiceBays);
    }

    [Fact]
    public async Task SetBayMaintenanceAsync_RestoresOutOfServiceBayWithoutClearingActiveLeases()
    {
        var graph = StandardTopologyFactory.CreateStandardGarage();
        var service = new ParkingAllocationService(
            graph,
            new AStarPathfinder(),
            new InMemoryBayLeaseManager(),
            new InMemoryEdgeCongestionTracker(),
            NullLogger<ParkingAllocationService>.Instance);

        var alloc = await service.AllocateAsync(new AllocateSpotRequest("GATE-N1", "CAR-KEEP", VehicleType.Standard));
        Assert.True(alloc.Succeeded);
        var leasedBayId = alloc.Session!.AssignedBayId;

        var freeBayId = graph.BaysById.Values.First(b => b.Status == BayStatus.Free).BayId;
        var outResult = await service.SetBayMaintenanceAsync(freeBayId, outOfService: true);
        Assert.Equal(BayStatus.OutOfService, outResult.NewStatus);

        var untouched = await service.SetBayMaintenanceAsync(leasedBayId, outOfService: false);
        Assert.Equal(BayStatus.Leased, untouched.NewStatus);

        var restored = await service.SetBayMaintenanceAsync(freeBayId, outOfService: false);
        Assert.Equal(BayStatus.Free, restored.NewStatus);
        Assert.Equal(BayStatus.Free, graph.BaysById[freeBayId].Status);
    }

    private sealed class CapturingEventSink : IParkingEventSink
    {
        public List<(string BayId, BayStatus Status)> BayChanges { get; } = [];
        public List<(string DisplacedBayId, string DisplacedSessionId, string? NewBayId)> Reassignments { get; } = [];

        public ValueTask OnBayAllocatedAsync(ActiveVehicleSession session, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask OnPhysicalOccupancyChangedAsync(string bayId, BayStatus newStatus, CancellationToken ct = default)
        {
            BayChanges.Add((bayId, newStatus));
            return ValueTask.CompletedTask;
        }

        public ValueTask OnRouteReassignedAsync(
            string displacedBayId,
            string displacedSessionId,
            ActiveVehicleSession? newSession,
            CancellationToken ct = default)
        {
            Reassignments.Add((displacedBayId, displacedSessionId, newSession?.AssignedBayId));
            return ValueTask.CompletedTask;
        }

        public ValueTask OnVehicleMovedAsync(string vehicleSessionId, string currentNodeId, CancellationToken ct = default) =>
            ValueTask.CompletedTask;
    }
}
