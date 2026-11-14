using System.Buffers;
using System.Runtime.CompilerServices;
using ParkMesh.Domain;

namespace ParkMesh.Application.Routing;

public sealed class AStarPathfinder : IPathfinder
{
    private const double NonPrimaryBayPenalty = 100.0;
    private const double ScoreTieTolerance = 1e-9;

    private static readonly ConditionalWeakTable<ParkingGraph, CompiledTopology> TopologyCache = new();

    public ParkingRoute? FindShortestPath(
        ParkingGraph graph,
        string startNodeId,
        string targetBayId,
        IReadOnlyDictionary<string, double>? edgeCongestion = null)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var topology = TopologyCache.GetValue(graph, static g => new CompiledTopology(g));

        if (!topology.NodeIndexById.TryGetValue(startNodeId, out var startIdx))
            return null;

        if (!graph.TryGetBay(targetBayId, out var targetBay) ||
            !topology.NodeIndexById.TryGetValue(targetBay.NodeId, out var targetIdx))
            return null;

        var startNode = topology.Nodes[startIdx];
        var targetNode = topology.Nodes[targetIdx];

        if (startIdx == targetIdx)
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

        var nodeCount = topology.Nodes.Length;
        var gScore = ArrayPool<double>.Shared.Rent(nodeCount);
        var closed = ArrayPool<bool>.Shared.Rent(nodeCount);
        var prevNode = ArrayPool<int>.Shared.Rent(nodeCount);
        var prevEdge = ArrayPool<GraphEdge?>.Shared.Rent(nodeCount);
        var prevCost = ArrayPool<double>.Shared.Rent(nodeCount);
        var congestionArr = RentCongestionArray(topology, edgeCongestion);

        try
        {
            Array.Fill(gScore, double.PositiveInfinity, 0, nodeCount);
            Array.Clear(closed, 0, nodeCount);
            Array.Fill(prevNode, -1, 0, nodeCount);
            Array.Clear(prevEdge, 0, nodeCount);
            Array.Clear(prevCost, 0, nodeCount);

            gScore[startIdx] = 0.0;
            var openSet = new PriorityQueue<int, double>();
            openSet.Enqueue(startIdx, ComputeHeuristic(startNode, targetNode));

            while (openSet.TryDequeue(out var currentIdx, out _))
            {
                if (closed[currentIdx])
                    continue;

                closed[currentIdx] = true;

                if (currentIdx == targetIdx)
                {
                    return ReconstructRoute(
                        topology,
                        targetBay,
                        targetIdx,
                        startIdx,
                        prevNode,
                        prevEdge,
                        prevCost,
                        gScore[currentIdx]);
                }

                var currentG = gScore[currentIdx];
                var outgoing = topology.OutgoingByNode[currentIdx];
                for (var i = 0; i < outgoing.Length; i++)
                {
                    var edge = outgoing[i];
                    var nextIdx = edge.ToNodeIndex;
                    if (closed[nextIdx])
                        continue;

                    var congestion = congestionArr is not null ? congestionArr[edge.Index] : 0.0;
                    var stepCost = edge.EffectiveBaseCost + congestion;
                    var tentativeG = currentG + stepCost;

                    if (tentativeG < gScore[nextIdx])
                    {
                        gScore[nextIdx] = tentativeG;
                        prevNode[nextIdx] = currentIdx;
                        prevEdge[nextIdx] = edge.Edge;
                        prevCost[nextIdx] = stepCost;

                        var fScore = tentativeG + ComputeHeuristic(topology.Nodes[nextIdx], targetNode);
                        openSet.Enqueue(nextIdx, fScore);
                    }
                }
            }

            return null;
        }
        finally
        {
            ArrayPool<double>.Shared.Return(gScore);
            ArrayPool<bool>.Shared.Return(closed);
            ArrayPool<int>.Shared.Return(prevNode);
            ArrayPool<GraphEdge?>.Shared.Return(prevEdge, clearArray: true);
            ArrayPool<double>.Shared.Return(prevCost);
            if (congestionArr is not null)
                ArrayPool<double>.Shared.Return(congestionArr);
        }
    }

    public IReadOnlyList<ParkingRoute> FindCandidateBayRoutes(
        ParkingGraph graph,
        string startNodeId,
        VehicleType vehicleType,
        IReadOnlyDictionary<string, double>? edgeCongestion = null,
        int maxCandidates = 10)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var topology = TopologyCache.GetValue(graph, static g => new CompiledTopology(g));

        if (!topology.NodeIndexById.TryGetValue(startNodeId, out var startIdx) || maxCandidates <= 0)
            return Array.Empty<ParkingRoute>();

        var nodeCount = topology.Nodes.Length;
        var gScore = ArrayPool<double>.Shared.Rent(nodeCount);
        var closed = ArrayPool<bool>.Shared.Rent(nodeCount);
        var prevNode = ArrayPool<int>.Shared.Rent(nodeCount);
        var prevEdge = ArrayPool<GraphEdge?>.Shared.Rent(nodeCount);
        var prevCost = ArrayPool<double>.Shared.Rent(nodeCount);
        var congestionArr = RentCongestionArray(topology, edgeCongestion);

        try
        {
            Array.Fill(gScore, double.PositiveInfinity, 0, nodeCount);
            Array.Clear(closed, 0, nodeCount);
            Array.Fill(prevNode, -1, 0, nodeCount);
            Array.Clear(prevEdge, 0, nodeCount);
            Array.Clear(prevCost, 0, nodeCount);

            gScore[startIdx] = 0.0;
            var openSet = new PriorityQueue<int, double>();
            openSet.Enqueue(startIdx, 0.0);

            var candidates = new List<CandidateMatch>(Math.Min(maxCandidates * 2, 64));
            var topScores = new PriorityQueue<double, double>();
            var kthWorstScore = double.PositiveInfinity;

            while (openSet.TryDequeue(out var currentIdx, out var currentDist))
            {
                if (closed[currentIdx])
                    continue;

                if (candidates.Count >= maxCandidates && currentDist > kthWorstScore + ScoreTieTolerance)
                    break;

                closed[currentIdx] = true;

                var bay = topology.BayByNodeIndex[currentIdx];
                if (bay is not null && bay.Status == BayStatus.Free && bay.SupportsVehicle(vehicleType))
                {
                    var prefPenalty = bay.PrimaryType == vehicleType ? 0.0 : NonPrimaryBayPenalty;
                    var rankingScore = currentDist + prefPenalty;
                    candidates.Add(new CandidateMatch(bay, currentIdx, currentDist, rankingScore));

                    topScores.Enqueue(rankingScore, -rankingScore);
                    if (topScores.Count > maxCandidates)
                        topScores.Dequeue();

                    if (topScores.Count == maxCandidates)
                        kthWorstScore = topScores.Peek();
                }

                var outgoing = topology.OutgoingByNode[currentIdx];
                for (var i = 0; i < outgoing.Length; i++)
                {
                    var edge = outgoing[i];
                    var nextIdx = edge.ToNodeIndex;
                    if (closed[nextIdx])
                        continue;

                    var congestion = congestionArr is not null ? congestionArr[edge.Index] : 0.0;
                    var stepCost = edge.EffectiveBaseCost + congestion;
                    var tentativeG = currentDist + stepCost;

                    if (tentativeG < gScore[nextIdx])
                    {
                        gScore[nextIdx] = tentativeG;
                        prevNode[nextIdx] = currentIdx;
                        prevEdge[nextIdx] = edge.Edge;
                        prevCost[nextIdx] = stepCost;
                        openSet.Enqueue(nextIdx, tentativeG);
                    }
                }
            }

            if (candidates.Count == 0)
                return Array.Empty<ParkingRoute>();

            candidates.Sort(static (left, right) =>
            {
                var cmp = left.RankingScore.CompareTo(right.RankingScore);
                return cmp != 0
                    ? cmp
                    : StringComparer.OrdinalIgnoreCase.Compare(left.Bay.BayId, right.Bay.BayId);
            });

            var resultCount = Math.Min(maxCandidates, candidates.Count);
            var routes = new List<ParkingRoute>(resultCount);
            for (var i = 0; i < resultCount; i++)
            {
                var match = candidates[i];
                routes.Add(ReconstructRoute(
                    topology,
                    match.Bay,
                    match.NodeIndex,
                    startIdx,
                    prevNode,
                    prevEdge,
                    prevCost,
                    match.TotalCost));
            }

            return routes;
        }
        finally
        {
            ArrayPool<double>.Shared.Return(gScore);
            ArrayPool<bool>.Shared.Return(closed);
            ArrayPool<int>.Shared.Return(prevNode);
            ArrayPool<GraphEdge?>.Shared.Return(prevEdge, clearArray: true);
            ArrayPool<double>.Shared.Return(prevCost);
            if (congestionArr is not null)
                ArrayPool<double>.Shared.Return(congestionArr);
        }
    }

    public static double ComputeHeuristic(GraphNode from, GraphNode to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var euclidean2D = Math.Sqrt(dx * dx + dy * dy);
        var floorDelta = Math.Abs(to.Floor - from.Floor);

        return euclidean2D + floorDelta * StandardTopologyFactory.MinRampFloorTransitionExtraCost;
    }

    private static double[]? RentCongestionArray(
        CompiledTopology topology,
        IReadOnlyDictionary<string, double>? edgeCongestion)
    {
        if (edgeCongestion is null || edgeCongestion.Count == 0)
            return null;

        var rented = ArrayPool<double>.Shared.Rent(topology.EdgeCount);
        Array.Clear(rented, 0, topology.EdgeCount);

        foreach (var kvp in edgeCongestion)
        {
            if (kvp.Value > 0.0 && topology.EdgeIndexById.TryGetValue(kvp.Key, out var edgeIdx))
            {
                rented[edgeIdx] = kvp.Value;
            }
        }

        return rented;
    }

    private static ParkingRoute ReconstructRoute(
        CompiledTopology topology,
        ParkingBay targetBay,
        int targetIdx,
        int startIdx,
        int[] prevNode,
        GraphEdge?[] prevEdge,
        double[] prevCost,
        double totalCost)
    {
        if (targetIdx == startIdx)
        {
            return new ParkingRoute(
                targetBay.BayId,
                targetBay.NodeId,
                targetBay.Floor,
                totalCost,
                [targetBay.NodeId],
                Array.Empty<RouteStep>(),
                Array.Empty<LedDirective>());
        }

        var depth = 0;
        for (var cursor = targetIdx; cursor != startIdx && cursor >= 0; cursor = prevNode[cursor])
            depth++;

        var nodeIds = new string[depth + 1];
        var steps = new RouteStep[depth];
        var ledCount = 0;

        var writeCursor = targetIdx;
        nodeIds[depth] = topology.Nodes[targetIdx].Id;

        for (var i = depth - 1; i >= 0; i--)
        {
            var fromIdx = prevNode[writeCursor];
            var fromNode = topology.Nodes[fromIdx];
            var toNode = topology.Nodes[writeCursor];
            var edge = prevEdge[writeCursor]!;

            nodeIds[i] = fromNode.Id;
            steps[i] = new RouteStep(
                edge.Id,
                fromNode.Id,
                toNode.Id,
                fromNode.Floor,
                edge.DirectionHint,
                prevCost[writeCursor]);

            if (!string.IsNullOrWhiteSpace(fromNode.LedPanelId))
                ledCount++;

            writeCursor = fromIdx;
        }

        var ledDirectives = ledCount == 0 ? Array.Empty<LedDirective>() : new LedDirective[ledCount];
        if (ledCount > 0)
        {
            var ledIndex = 0;
            for (var i = 0; i < steps.Length; i++)
            {
                var fromNode = topology.Nodes[topology.NodeIndexById[steps[i].FromNodeId]];
                if (!string.IsNullOrWhiteSpace(fromNode.LedPanelId))
                {
                    ledDirectives[ledIndex++] = new LedDirective(fromNode.LedPanelId, fromNode.Floor, steps[i].Direction);
                }
            }
        }

        return new ParkingRoute(
            targetBay.BayId,
            targetBay.NodeId,
            targetBay.Floor,
            totalCost,
            nodeIds,
            steps,
            ledDirectives);
    }

    private readonly record struct CandidateMatch(
        ParkingBay Bay,
        int NodeIndex,
        double TotalCost,
        double RankingScore);

    private readonly record struct CompiledEdge(
        int Index,
        int ToNodeIndex,
        GraphEdge Edge,
        double EffectiveBaseCost);

    private sealed class CompiledTopology
    {
        public CompiledTopology(ParkingGraph graph)
        {
            var nodeList = graph.Nodes.Values.ToArray();
            Nodes = nodeList;
            NodeIndexById = new Dictionary<string, int>(nodeList.Length, StringComparer.OrdinalIgnoreCase);
            BayByNodeIndex = new ParkingBay?[nodeList.Length];

            for (var i = 0; i < nodeList.Length; i++)
            {
                var node = nodeList[i];
                NodeIndexById[node.Id] = i;
                if (graph.BaysByNodeId.TryGetValue(node.Id, out var bay))
                    BayByNodeIndex[i] = bay;
            }

            EdgeCount = graph.Edges.Count;
            EdgeIndexById = new Dictionary<string, int>(EdgeCount, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < EdgeCount; i++)
                EdgeIndexById[graph.Edges[i].Id] = i;

            OutgoingByNode = new CompiledEdge[nodeList.Length][];
            for (var i = 0; i < nodeList.Length; i++)
            {
                var outgoing = graph.GetOutgoingEdges(nodeList[i].Id);
                var compiled = new CompiledEdge[outgoing.Count];
                for (var j = 0; j < outgoing.Count; j++)
                {
                    var edge = outgoing[j];
                    compiled[j] = new CompiledEdge(
                        EdgeIndexById[edge.Id],
                        NodeIndexById[edge.ToNodeId],
                        edge,
                        edge.EffectiveBaseCost);
                }

                OutgoingByNode[i] = compiled;
            }
        }

        public GraphNode[] Nodes { get; }
        public Dictionary<string, int> NodeIndexById { get; }
        public ParkingBay?[] BayByNodeIndex { get; }
        public Dictionary<string, int> EdgeIndexById { get; }
        public CompiledEdge[][] OutgoingByNode { get; }
        public int EdgeCount { get; }
    }
}
