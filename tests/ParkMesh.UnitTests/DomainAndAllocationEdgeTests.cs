using Microsoft.Extensions.Logging.Abstractions;
using ParkMesh.Application.Abstractions;
using ParkMesh.Application.Allocation;
using ParkMesh.Application.Routing;
using ParkMesh.Application.Telemetry;
using ParkMesh.Domain;
using ParkMesh.Infrastructure.Redis;

namespace ParkMesh.UnitTests;

public sealed class DomainAndAllocationEdgeTests
{
    [Fact]
    public void ParkingBay_EnforcesMonotonicFencingAndStateTransitions()
    {
        var bay = new ParkingBay("F0-B01", "F0-BAY-01", 0, 10, 10, VehicleType.Standard, [VehicleType.EV]);
        var expiry = DateTimeOffset.UtcNow.AddMinutes(5);

        Assert.True(bay.SupportsVehicle(VehicleType.Standard));
        Assert.True(bay.SupportsVehicle(VehicleType.EV));
        Assert.False(bay.SupportsVehicle(VehicleType.Moto));

        Assert.True(bay.TrySetLeased("CAR-1", 5, expiry));
        Assert.False(bay.TrySetLeased("CAR-2", 6, expiry));

        Assert.False(bay.TryReleaseLease(4));
        Assert.Equal(BayStatus.Leased, bay.Status);
        Assert.Equal("CAR-1", bay.CurrentSessionId);

        Assert.True(bay.TryReleaseLease(5));
        Assert.Equal(BayStatus.Free, bay.Status);

        Assert.False(bay.TrySetLeased("CAR-STALE", 5, expiry));
        Assert.False(bay.TrySetLeased("CAR-STALE", 3, expiry));
        Assert.True(bay.TrySetLeased("CAR-FRESH", 6, expiry));

        bay.MarkOutOfService();
        Assert.Equal(BayStatus.OutOfService, bay.Status);
        Assert.Equal(6, bay.FencingToken);
        Assert.False(bay.TrySetLeased("CAR-BLOCKED", 7, expiry));

        bay.MarkFree();
        Assert.False(bay.TrySetLeased("CAR-REPLAY", 6, expiry));
        Assert.True(bay.TrySetLeased("CAR-VALID", 7, expiry));
    }

    [Fact]
    public async Task ParkingAllocationService_HandlesCapacityExhaustionInvalidInputsAndContention()
    {
        var graph = StandardTopologyFactory.CreateStandardGarage();
        var service = new ParkingAllocationService(
            graph,
            new AStarPathfinder(),
            new InMemoryBayLeaseManager(),
            new InMemoryEdgeCongestionTracker(),
            NullLogger<ParkingAllocationService>.Instance);

        var unknownNodeResult = await service.AllocateAsync(new AllocateSpotRequest("NO-SUCH-GATE", "CAR-ERR", VehicleType.Standard));
        Assert.False(unknownNodeResult.Succeeded);

        Assert.False(service.UpdateVehicleProgress("MISSING-SESSION", "GATE-N1"));
        Assert.False(await service.UpdateVehicleProgressAsync("CAR-ERR", "NO-SUCH-NODE"));
        Assert.False(await service.ReleaseLeaseAsync("NO-SUCH-BAY", 1));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.SetBayMaintenanceAsync("NO-SUCH-BAY", true));

        var batch1 = await Task.WhenAll(Enumerable.Range(1, 60)
            .Select(i => service.AllocateAsync(new AllocateSpotRequest(i % 2 == 0 ? "GATE-N1" : "GATE-S1", $"BURST-A-{i:D2}", VehicleType.Standard))));
        Assert.All(batch1, r => Assert.True(r.Succeeded, r.FailureReason));

        var assignedIds = batch1.Select(r => r.Session!.AssignedBayId).ToArray();
        Assert.Equal(60, assignedIds.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var bay in graph.BaysById.Values.Where(b => b.Status == BayStatus.Free))
        {
            bay.MarkOccupied("PREFILLED");
        }

        var fullResult = await service.AllocateAsync(new AllocateSpotRequest("GATE-N1", "CAR-OVERFLOW", VehicleType.Standard));
        Assert.False(fullResult.Succeeded);
        Assert.Null(fullResult.Session);

        var staleRelease = await service.ReleaseLeaseAsync(batch1[0].Session!.AssignedBayId, batch1[0].Session!.FencingToken + 99);
        Assert.False(staleRelease);
        Assert.Equal(BayStatus.Leased, graph.BaysById[batch1[0].Session!.AssignedBayId].Status);
    }

    [Fact]
    public void Pathfinder_ReturnsNullForUnknownNodesAndHandlesZeroStepRoutes()
    {
        var graph = StandardTopologyFactory.CreateStandardGarage();
        var pathfinder = new AStarPathfinder();

        Assert.Null(pathfinder.FindShortestPath(graph, "UNKNOWN-NODE", "F0-B01"));
        Assert.Null(pathfinder.FindShortestPath(graph, "GATE-N1", "UNKNOWN-BAY"));

        var zeroHop = pathfinder.FindShortestPath(graph, graph.BaysById["F0-B01"].NodeId, "F0-B01");
        Assert.NotNull(zeroHop);
        Assert.Empty(zeroHop!.Steps);
        Assert.Equal(0, zeroHop.TotalCost);
    }

    [Fact]
    public void SensorReadingFilter_SupportsFullOccupiedToFreeToOccupiedCycleIndependentlyPerBay()
    {
        var filter = new SensorReadingFilter(new SensorDebounceOptions
        {
            MinConsecutiveReadings = 2,
            StabilityWindowMs = 1000
        });

        var t0 = DateTimeOffset.UtcNow;

        Assert.Equal(SensorFilterOutcome.Debouncing, filter.Evaluate(new BaySensorReading("F0-B01", true, 30, 1, t0)));
        Assert.Equal(SensorFilterOutcome.Debouncing, filter.Evaluate(new BaySensorReading("F0-B02", true, 30, 1, t0)));

        Assert.Equal(SensorFilterOutcome.ConfirmedTransition, filter.Evaluate(new BaySensorReading("F0-B01", true, 29, 2, t0.AddSeconds(2))));
        Assert.Equal(SensorFilterOutcome.Unchanged, filter.Evaluate(new BaySensorReading("F0-B01", true, 31, 3, t0.AddSeconds(3))));

        Assert.Equal(SensorFilterOutcome.Debouncing, filter.Evaluate(new BaySensorReading("F0-B01", false, 200, 4, t0.AddSeconds(4))));
        Assert.Equal(SensorFilterOutcome.ConfirmedTransition, filter.Evaluate(new BaySensorReading("F0-B01", false, 200, 5, t0.AddSeconds(6))));

        Assert.Equal(SensorFilterOutcome.Unchanged, filter.Evaluate(new BaySensorReading("F0-B02", false, 200, 2, t0.AddSeconds(1))));
        Assert.Equal(SensorFilterOutcome.Debouncing, filter.Evaluate(new BaySensorReading("F0-B02", true, 30, 3, t0.AddSeconds(2))));
        Assert.Equal(SensorFilterOutcome.ConfirmedTransition, filter.Evaluate(new BaySensorReading("F0-B02", true, 30, 4, t0.AddSeconds(4))));
    }

    [Fact]
    public void SlidingLatencyWindow_HandlesWrapAroundAboveCapacityWithoutDrift()
    {
        var window = new SlidingLatencyWindow(32);

        for (var i = 1; i <= 100; i++)
        {
            window.Add(i);
        }

        var percentiles = window.ComputePercentiles();
        Assert.Equal(32, percentiles.SampleCount);
        Assert.InRange(percentiles.P50Ms, 83.0, 86.0);
        Assert.InRange(percentiles.P95Ms, 98.0, 100.0);
        Assert.InRange(percentiles.P99Ms, 99.0, 100.0);
    }
}
