using ParkMesh.Domain;

namespace ParkMesh.Application.Routing;

public sealed class AStarPathfinder : IPathfinder
{
    private const double NonPrimaryBayPenalty = 100.0;

    public ParkingRoute? FindShortestPath(
        ParkingGraph graph,
        string startNodeId,
        string targetBayId,
        IReadOnlyDictionary<string, double>? edgeCongestion = null)
    {
        ArgumentNullException.ThrowIfNull(graph);

        if (!graph.TryGetNode(startNodeId, out var startNode))
            return null;

        if (!graph.TryGetBay(targetBayId, out var targetBay) ||
            !graph.TryGetNode(targetBay.NodeId, out var targetNode))
            return null;

        if (string.Equals(startNode.Id, targetNode.Id, StringComparison.OrdinalIgnoreCase))
        {
            return new ParkingRoute(
                targetBay.BayId,
                targetNode.Id,
                targetBay.Floor,
                0.0,
                [startNode.Id],
                Array.Empty<RouteStep>(),
                Array.Empty<LedDirective>());
        }

        var openSet = new PriorityQueue<string, double>();
        var gScore = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            [startNode.Id] = 0.0
        };
        var cameFrom = new Dictionary<string, (string PrevNodeId, GraphEdge Edge, double StepCost)>(StringComparer.OrdinalIgnoreCase);
        var closed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        openSet.Enqueue(startNode.Id, ComputeHeuristic(startNode, targetNode));

        while (openSet.TryDequeue(out var currentId, out _))
        {
            if (!closed.Add(currentId))
                continue;

            if (string.Equals(currentId, targetNode.Id, StringComparison.OrdinalIgnoreCase))
            {
                return ReconstructRoute(graph, targetBay, cameFrom, gScore[currentId]);
            }

            var currentG = gScore[currentId];
            foreach (var edge in graph.GetOutgoingEdges(currentId))
            {
                if (closed.Contains(edge.ToNodeId))
                    continue;

                var congestion = edgeCongestion is not null && edgeCongestion.TryGetValue(edge.Id, out var c)
                    ? c
                    : 0.0;

                var stepCost = edge.EffectiveBaseCost + Math.Max(0.0, congestion);
                var tentativeG = currentG + stepCost;

                if (!gScore.TryGetValue(edge.ToNodeId, out var existingG) || tentativeG < existingG)
                {
                    gScore[edge.ToNodeId] = tentativeG;
                    cameFrom[edge.ToNodeId] = (currentId, edge, stepCost);

                    var nextNode = graph.Nodes[edge.ToNodeId];
                    var fScore = tentativeG + ComputeHeuristic(nextNode, targetNode);
                    openSet.Enqueue(edge.ToNodeId, fScore);
                }
            }
        }

        return null;
    }

    public IReadOnlyList<ParkingRoute> FindCandidateBayRoutes(
        ParkingGraph graph,
        string startNodeId,
        VehicleType vehicleType,
        IReadOnlyDictionary<string, double>? edgeCongestion = null,
        int maxCandidates = 10)
    {
        ArgumentNullException.ThrowIfNull(graph);

        if (!graph.TryGetNode(startNodeId, out var startNode) || maxCandidates <= 0)
            return Array.Empty<ParkingRoute>();

        var compatibleBays = graph.BaysById.Values
            .Where(b => b.Status == BayStatus.Free && b.SupportsVehicle(vehicleType))
            .Select(bay =>
            {
                var bayNode = graph.Nodes[bay.NodeId];
                var h = ComputeHeuristic(startNode, bayNode);
                var prefPenalty = bay.PrimaryType == vehicleType ? 0.0 : NonPrimaryBayPenalty;
                return (Bay: bay, EstimatedScore: h + prefPenalty);
            })
            .OrderBy(x => x.EstimatedScore)
            .ToList();

        if (compatibleBays.Count == 0)
            return Array.Empty<ParkingRoute>();

        var evalLimit = Math.Min(compatibleBays.Count, Math.Max(maxCandidates * 3, 24));
        var routes = new List<(ParkingRoute Route, double RankingScore)>(evalLimit);

        for (var i = 0; i < evalLimit; i++)
        {
            var candidate = compatibleBays[i];
            var route = FindShortestPath(graph, startNodeId, candidate.Bay.BayId, edgeCongestion);
            if (route is null)
                continue;

            var prefPenalty = candidate.Bay.PrimaryType == vehicleType ? 0.0 : NonPrimaryBayPenalty;
            routes.Add((route, route.TotalCost + prefPenalty));
        }

        return routes
            .OrderBy(r => r.RankingScore)
            .ThenBy(r => r.Route.TargetBayId, StringComparer.OrdinalIgnoreCase)
            .Take(maxCandidates)
            .Select(r => r.Route)
            .ToList();
    }

    public static double ComputeHeuristic(GraphNode from, GraphNode to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var euclidean2D = Math.Sqrt(dx * dx + dy * dy);
        var floorDelta = Math.Abs(to.Floor - from.Floor);

        return euclidean2D + floorDelta * StandardTopologyFactory.MinRampFloorTransitionExtraCost;
    }

    private static ParkingRoute ReconstructRoute(
        ParkingGraph graph,
        ParkingBay targetBay,
        Dictionary<string, (string PrevNodeId, GraphEdge Edge, double StepCost)> cameFrom,
        double totalCost)
    {
        var stepsReversed = new List<RouteStep>();
        var nodesReversed = new List<string> { targetBay.NodeId };

        var cursor = targetBay.NodeId;
        while (cameFrom.TryGetValue(cursor, out var hop))
        {
            var fromNode = graph.Nodes[hop.PrevNodeId];
            stepsReversed.Add(new RouteStep(
                hop.Edge.Id,
                hop.PrevNodeId,
                cursor,
                fromNode.Floor,
                hop.Edge.DirectionHint,
                hop.StepCost));

            nodesReversed.Add(hop.PrevNodeId);
            cursor = hop.PrevNodeId;
        }

        nodesReversed.Reverse();
        stepsReversed.Reverse();

        var ledDirectives = new List<LedDirective>();
        foreach (var step in stepsReversed)
        {
            var node = graph.Nodes[step.FromNodeId];
            if (!string.IsNullOrWhiteSpace(node.LedPanelId))
            {
                ledDirectives.Add(new LedDirective(node.LedPanelId, node.Floor, step.Direction));
            }
        }

        return new ParkingRoute(
            targetBay.BayId,
            targetBay.NodeId,
            targetBay.Floor,
            totalCost,
            nodesReversed,
            stepsReversed,
            ledDirectives);
    }
}
