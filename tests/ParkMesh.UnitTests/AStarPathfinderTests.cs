using ParkMesh.Application.Routing;
using ParkMesh.Domain;

namespace ParkMesh.UnitTests;

public sealed class AStarPathfinderTests
{
    [Fact]
    public void FindShortestPath_MatchesExactDijkstraCost_AcrossAllFloorsAndRandomCongestion()
    {
        var graph = StandardTopologyFactory.CreateStandardGarage();
        var pathfinder = new AStarPathfinder();
        var rng = new Random(42);

        var congestion = graph.Edges.ToDictionary(
            e => e.Id,
            _ => rng.NextDouble() * 30.0,
            StringComparer.OrdinalIgnoreCase);

        var sampleBayIds = new[]
        {
            "F0-B01", "F0-B15", "F0-B40",
            "F1-B02", "F1-B22", "F1-B39",
            "F2-B05", "F2-B19", "F2-B40"
        };

        foreach (var gate in graph.Gates)
        {
            var dijkstraDist = ComputeDijkstraDistances(graph, gate.Id, congestion);

            foreach (var bayId in sampleBayIds)
            {
                var bayNodeId = graph.BaysById[bayId].NodeId;
                var route = pathfinder.FindShortestPath(graph, gate.Id, bayId, congestion);

                Assert.NotNull(route);
                Assert.Equal(dijkstraDist[bayNodeId], route.TotalCost, precision: 6);
            }
        }
    }

    [Fact]
    public void FindShortestPath_ToUpperFloor_UsesUpRampAndEmitsLedDirectives()
    {
        var graph = StandardTopologyFactory.CreateStandardGarage();
        var pathfinder = new AStarPathfinder();

        var route = pathfinder.FindShortestPath(graph, "GATE-N1", "F2-B25");

        Assert.NotNull(route);
        Assert.Equal(2, route.TargetFloor);
        Assert.Equal(2, route.Steps.Count(s => s.Direction == DirectionHint.UpRamp));
        Assert.NotEmpty(route.LedDirectives);
    }

    [Fact]
    public void FindShortestPath_ReroutesAroundHeavilyCongestedNorthCorridor()
    {
        var graph = StandardTopologyFactory.CreateStandardGarage();
        var pathfinder = new AStarPathfinder();

        var baseline = pathfinder.FindShortestPath(graph, "GATE-N1", "F0-B09");
        Assert.NotNull(baseline);
        Assert.Contains("F0-I-N-1", baseline.NodeIds);

        var congestion = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["E-F0-I-W-N-TO-F0-I-N-1"] = 500.0
        };

        var detoured = pathfinder.FindShortestPath(graph, "GATE-N1", "F0-B09", congestion);
        Assert.NotNull(detoured);
        Assert.DoesNotContain("E-F0-I-W-N-TO-F0-I-N-1", detoured.Steps.Select(s => s.EdgeId));
        Assert.Contains("F0-I-W-C", detoured.NodeIds);
    }

    [Fact]
    public void FindCandidateBayRoutes_PrefersPrimaryVehicleType_OverFallbackCompatibleBay()
    {
        var graph = StandardTopologyFactory.CreateStandardGarage();
        var pathfinder = new AStarPathfinder();

        var routes = pathfinder.FindCandidateBayRoutes(graph, "GATE-N1", VehicleType.Standard, maxCandidates: 5);

        Assert.NotEmpty(routes);
        Assert.All(routes, r =>
        {
            var bay = graph.BaysById[r.TargetBayId];
            Assert.Equal(VehicleType.Standard, bay.PrimaryType);
        });
    }

    [Fact]
    public void FindCandidateBayRoutes_MatchesExactGlobalDijkstraRanking_AcrossCongestionAndFallbackBays()
    {
        var graph = StandardTopologyFactory.CreateStandardGarage();
        var pathfinder = new AStarPathfinder();
        var rng = new Random(99);

        var congestion = graph.Edges.ToDictionary(
            e => e.Id,
            _ => rng.NextDouble() * 40.0,
            StringComparer.OrdinalIgnoreCase);

        foreach (var bay in graph.BaysById.Values.Where(b => b.PrimaryType == VehicleType.EV).Take(6))
        {
            bay.MarkOccupied("OCCUPIED-TEST");
        }

        var expectedDist = ComputeDijkstraDistances(graph, "GATE-N1", congestion);
        var expectedTop5 = graph.BaysById.Values
            .Where(b => b.Status == BayStatus.Free && b.SupportsVehicle(VehicleType.EV))
            .Select(b =>
            {
                var totalCost = expectedDist[b.NodeId];
                var score = totalCost + (b.PrimaryType == VehicleType.EV ? 0.0 : 100.0);
                return (b.BayId, TotalCost: totalCost, Score: score);
            })
            .OrderBy(x => x.Score)
            .ThenBy(x => x.BayId, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToArray();

        var actualTop5 = pathfinder.FindCandidateBayRoutes(graph, "GATE-N1", VehicleType.EV, congestion, maxCandidates: 5);

        Assert.Equal(expectedTop5.Length, actualTop5.Count);
        for (var i = 0; i < expectedTop5.Length; i++)
        {
            Assert.Equal(expectedTop5[i].BayId, actualTop5[i].TargetBayId);
            Assert.Equal(expectedTop5[i].TotalCost, actualTop5[i].TotalCost, precision: 6);
        }
    }

    private static Dictionary<string, double> ComputeDijkstraDistances(
        ParkingGraph graph,
        string startNodeId,
        IReadOnlyDictionary<string, double> congestion)
    {
        var dist = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            [startNodeId] = 0.0
        };
        var pq = new PriorityQueue<string, double>();
        pq.Enqueue(startNodeId, 0.0);

        while (pq.TryDequeue(out var current, out var currentDist))
        {
            if (currentDist > dist[current])
                continue;

            foreach (var edge in graph.GetOutgoingEdges(current))
            {
                var weight = edge.EffectiveBaseCost + (congestion.TryGetValue(edge.Id, out var c) ? c : 0.0);
                var nextDist = currentDist + weight;

                if (!dist.TryGetValue(edge.ToNodeId, out var existing) || nextDist < existing)
                {
                    dist[edge.ToNodeId] = nextDist;
                    pq.Enqueue(edge.ToNodeId, nextDist);
                }
            }
        }

        return dist;
    }
}
