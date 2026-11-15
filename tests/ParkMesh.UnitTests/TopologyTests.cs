using ParkMesh.Application.Abstractions;
using ParkMesh.Application.Routing;
using ParkMesh.Domain;

namespace ParkMesh.UnitTests;

public sealed class TopologyTests
{
    [Fact]
    public void StandardGarage_Has3Floors_120Bays_And2Gates()
    {
        var graph = StandardTopologyFactory.CreateStandardGarage();

        Assert.Equal(0, graph.MinFloor);
        Assert.Equal(2, graph.MaxFloor);
        Assert.Equal(120, graph.BaysById.Count);
        Assert.Equal(2, graph.Gates.Count);
        Assert.Contains(graph.Gates, g => g.Id == "GATE-N1");
        Assert.Contains(graph.Gates, g => g.Id == "GATE-S1");
    }

    [Fact]
    public void All120Bays_AreReachableFromBothGates()
    {
        var graph = StandardTopologyFactory.CreateStandardGarage();
        var pathfinder = new AStarPathfinder();

        foreach (var gate in graph.Gates)
        {
            foreach (var bay in graph.BaysById.Values)
            {
                var route = pathfinder.FindShortestPath(graph, gate.Id, bay.BayId);
                Assert.NotNull(route);
                Assert.Equal(bay.BayId, route.TargetBayId);
                Assert.Equal(gate.Id, route.NodeIds[0]);
                Assert.Equal(bay.NodeId, route.NodeIds[^1]);
            }
        }
    }

    [Theory]
    [InlineData(VehicleType.Pcd, 12)]
    [InlineData(VehicleType.EV, 18)]
    [InlineData(VehicleType.SUV, 18)]
    [InlineData(VehicleType.Moto, 12)]
    [InlineData(VehicleType.Standard, 78)]
    public void StandardGarage_SupportsExpectedBayCountsPerVehicleType(VehicleType vehicleType, int expectedCount)
    {
        var graph = StandardTopologyFactory.CreateStandardGarage();
        var supportedCount = graph.BaysById.Values.Count(b => b.SupportsVehicle(vehicleType));
        Assert.Equal(expectedCount, supportedCount);
    }

    [Fact]
    public void ParkingGraph_RejectsEdgesWithUnknownNodes_AndClonesIndependentBayStatuses()
    {
        var node = new GraphNode("N-1", 0, 0, 0, NodeType.Intersection);

        Assert.Throws<InvalidOperationException>(() =>
            new ParkingGraph([node], [new GraphEdge("E-BAD-SRC", "MISSING-SRC", "N-1", 10)], []));
        Assert.Throws<InvalidOperationException>(() =>
            new ParkingGraph([node], [new GraphEdge("E-BAD-DST", "N-1", "MISSING-DST", 10)], []));

        var original = StandardTopologyFactory.CreateStandardGarage();
        var cloned = original.CloneTopology();

        Assert.Empty(original.GetOutgoingEdges("NO-SUCH-NODE"));

        cloned.BaysById["F0-B01"].MarkOccupied("CLONE-SESSION");
        Assert.Equal(BayStatus.Occupied, cloned.BaysById["F0-B01"].Status);
        Assert.Equal(BayStatus.Free, original.BaysById["F0-B01"].Status);
    }

    [Fact]
    public void InMemoryEdgeCongestionTracker_AccumulatesAndClampsPenaltiesAtZero()
    {
        var tracker = new InMemoryEdgeCongestionTracker();

        Assert.Equal(0.0, tracker.GetPenalty("UNKNOWN-EDGE"));

        tracker.AddRouteLoad(["E-NORTH-1", "E-NORTH-2"], weightPerVehicle: 6.0);
        tracker.AddRouteLoad(["e-north-1"], weightPerVehicle: 4.0);

        Assert.Equal(10.0, tracker.GetPenalty("E-NORTH-1"));
        Assert.Equal(6.0, tracker.GetPenalty("E-NORTH-2"));

        tracker.RemoveRouteLoad(["E-NORTH-1", "E-NORTH-2"], weightPerVehicle: 15.0);

        Assert.Equal(0.0, tracker.GetPenalty("E-NORTH-1"));
        Assert.Equal(0.0, tracker.GetPenalty("E-NORTH-2"));
        Assert.All(tracker.Snapshot().Values, v => Assert.Equal(0.0, v));
    }
}
