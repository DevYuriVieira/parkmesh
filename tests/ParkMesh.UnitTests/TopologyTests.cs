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
}
