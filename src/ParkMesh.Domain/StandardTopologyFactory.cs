namespace ParkMesh.Domain;

public static class StandardTopologyFactory
{
    public const int FloorCount = 3;
    public const int BaysPerFloor = 40;
    public const double MinRampFloorTransitionExtraCost = 40.0;

    private static readonly double[] BayColumnXs = [20, 35, 50, 65, 80];

    public static ParkingGraph CreateStandardGarage()
    {
        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();
        var bays = new List<ParkingBay>();

        for (var floor = 0; floor < FloorCount; floor++)
        {
            BuildFloorTopology(floor, nodes, edges, bays);
        }

        nodes.Add(new GraphNode("GATE-N1", 0, 0, 10, NodeType.Gate));
        edges.Add(new GraphEdge("E-GATE-N1", "GATE-N1", "F0-I-W-N", 10, DirectionHint: DirectionHint.Straight));

        nodes.Add(new GraphNode("GATE-S1", 0, 0, 50, NodeType.Gate));
        edges.Add(new GraphEdge("E-GATE-S1", "GATE-S1", "F0-I-W-S", 10, DirectionHint: DirectionHint.Straight));

        for (var floor = 0; floor < FloorCount - 1; floor++)
        {
            var upFrom = $"F{floor}-RAMP-UP";
            var upTo = $"F{floor + 1}-RAMP-UP-OUT";
            nodes.Add(new GraphNode(upFrom, floor, 95, 30, NodeType.Ramp));
            nodes.Add(new GraphNode(upTo, floor + 1, 95, 30, NodeType.Ramp));

            edges.Add(new GraphEdge($"E-F{floor}-TO-RAMP-UP", $"F{floor}-I-E-C", upFrom, 5, DirectionHint: DirectionHint.Right));
            edges.Add(new GraphEdge($"E-RAMP-UP-F{floor}-F{floor + 1}", upFrom, upTo, 25, IsRamp: true, RampPenalty: 2.0, DirectionHint: DirectionHint.UpRamp));
            edges.Add(new GraphEdge($"E-RAMP-UP-EXIT-F{floor + 1}", upTo, $"F{floor + 1}-I-E-C", 5, DirectionHint: DirectionHint.Straight));

            var dnFrom = $"F{floor + 1}-RAMP-DN";
            var dnTo = $"F{floor}-RAMP-DN-OUT";
            nodes.Add(new GraphNode(dnFrom, floor + 1, 5, 30, NodeType.Ramp));
            nodes.Add(new GraphNode(dnTo, floor, 5, 30, NodeType.Ramp));

            edges.Add(new GraphEdge($"E-F{floor + 1}-TO-RAMP-DN", $"F{floor + 1}-I-W-C", dnFrom, 5, DirectionHint: DirectionHint.Left));
            edges.Add(new GraphEdge($"E-RAMP-DN-F{floor + 1}-F{floor}", dnFrom, dnTo, 25, IsRamp: true, RampPenalty: 1.6, DirectionHint: DirectionHint.DownRamp));
            edges.Add(new GraphEdge($"E-RAMP-DN-EXIT-F{floor}", dnTo, $"F{floor}-I-W-C", 5, DirectionHint: DirectionHint.Straight));
        }

        return new ParkingGraph(nodes, edges, bays);
    }

    private static void BuildFloorTopology(
        int floor,
        List<GraphNode> nodes,
        List<GraphEdge> edges,
        List<ParkingBay> bays)
    {
        var wn = $"F{floor}-I-W-N";
        var wc = $"F{floor}-I-W-C";
        var ws = $"F{floor}-I-W-S";
        var mc = $"F{floor}-I-M-C";
        var en = $"F{floor}-I-E-N";
        var ec = $"F{floor}-I-E-C";
        var es = $"F{floor}-I-E-S";

        nodes.Add(new GraphNode(wn, floor, 10, 10, NodeType.Intersection, $"LED-F{floor}-WN"));
        nodes.Add(new GraphNode(wc, floor, 10, 30, NodeType.Intersection, $"LED-F{floor}-WC"));
        nodes.Add(new GraphNode(ws, floor, 10, 50, NodeType.Intersection, $"LED-F{floor}-WS"));
        nodes.Add(new GraphNode(mc, floor, 50, 30, NodeType.Intersection, $"LED-F{floor}-MC"));
        nodes.Add(new GraphNode(en, floor, 90, 10, NodeType.Intersection, $"LED-F{floor}-EN"));
        nodes.Add(new GraphNode(ec, floor, 90, 30, NodeType.Intersection, $"LED-F{floor}-EC"));
        nodes.Add(new GraphNode(es, floor, 90, 50, NodeType.Intersection, $"LED-F{floor}-ES"));

        var northNodes = new List<string>(BayColumnXs.Length);
        var southNodes = new List<string>(BayColumnXs.Length);

        for (var i = 0; i < BayColumnXs.Length; i++)
        {
            var x = BayColumnXs[i];
            var nId = $"F{floor}-I-N-{i + 1}";
            var sId = $"F{floor}-I-S-{i + 1}";

            nodes.Add(new GraphNode(nId, floor, x, 10, NodeType.Intersection, i == 2 ? $"LED-F{floor}-NC" : null));
            nodes.Add(new GraphNode(sId, floor, x, 50, NodeType.Intersection, i == 2 ? $"LED-F{floor}-SC" : null));

            northNodes.Add(nId);
            southNodes.Add(sId);
        }

        AddDirectedSegment(edges, nodes, wn, northNodes[0], DirectionHint.Straight);
        for (var i = 0; i < northNodes.Count - 1; i++)
        {
            AddDirectedSegment(edges, nodes, northNodes[i], northNodes[i + 1], DirectionHint.Straight);
        }
        AddDirectedSegment(edges, nodes, northNodes[^1], en, DirectionHint.Straight);

        AddDirectedSegment(edges, nodes, es, southNodes[^1], DirectionHint.Straight);
        for (var i = southNodes.Count - 1; i > 0; i--)
        {
            AddDirectedSegment(edges, nodes, southNodes[i], southNodes[i - 1], DirectionHint.Straight);
        }
        AddDirectedSegment(edges, nodes, southNodes[0], ws, DirectionHint.Straight);

        AddBidirectionalSegment(edges, nodes, wc, mc, DirectionHint.Straight, DirectionHint.Straight);
        AddBidirectionalSegment(edges, nodes, mc, ec, DirectionHint.Straight, DirectionHint.Straight);

        AddBidirectionalSegment(edges, nodes, wn, wc, DirectionHint.Right, DirectionHint.Left);
        AddBidirectionalSegment(edges, nodes, wc, ws, DirectionHint.Straight, DirectionHint.Straight);

        AddBidirectionalSegment(edges, nodes, northNodes[2], mc, DirectionHint.Right, DirectionHint.Left);
        AddBidirectionalSegment(edges, nodes, mc, southNodes[2], DirectionHint.Straight, DirectionHint.Straight);

        AddBidirectionalSegment(edges, nodes, en, ec, DirectionHint.Right, DirectionHint.Left);
        AddBidirectionalSegment(edges, nodes, ec, es, DirectionHint.Straight, DirectionHint.Straight);

        var bayIndex = 1;
        for (var col = 0; col < BayColumnXs.Length; col++)
        {
            var x = BayColumnXs[col];
            var parentNodeId = northNodes[col];

            AddBay(floor, ref bayIndex, parentNodeId, x - 2.5, 4, nodes, edges, bays);
            AddBay(floor, ref bayIndex, parentNodeId, x + 2.5, 4, nodes, edges, bays);
            AddBay(floor, ref bayIndex, parentNodeId, x - 2.5, 16, nodes, edges, bays);
            AddBay(floor, ref bayIndex, parentNodeId, x + 2.5, 16, nodes, edges, bays);
        }

        for (var col = 0; col < BayColumnXs.Length; col++)
        {
            var x = BayColumnXs[col];
            var parentNodeId = southNodes[col];

            AddBay(floor, ref bayIndex, parentNodeId, x - 2.5, 44, nodes, edges, bays);
            AddBay(floor, ref bayIndex, parentNodeId, x + 2.5, 44, nodes, edges, bays);
            AddBay(floor, ref bayIndex, parentNodeId, x - 2.5, 56, nodes, edges, bays);
            AddBay(floor, ref bayIndex, parentNodeId, x + 2.5, 56, nodes, edges, bays);
        }
    }

    private static void AddBay(
        int floor,
        ref int bayIndex,
        string parentNodeId,
        double x,
        double y,
        List<GraphNode> nodes,
        List<GraphEdge> edges,
        List<ParkingBay> bays)
    {
        var bayId = $"F{floor}-B{bayIndex:D2}";
        var nodeId = $"NODE-{bayId}";
        var (primaryType, allowedTypes) = ResolveBayVehicleTypes(bayIndex);

        var parent = nodes.First(n => n.Id == parentNodeId);
        var distance = Math.Sqrt(Math.Pow(x - parent.X, 2) + Math.Pow(y - parent.Y, 2));

        nodes.Add(new GraphNode(nodeId, floor, x, y, NodeType.Bay));
        edges.Add(new GraphEdge($"E-{parentNodeId}-TO-{bayId}", parentNodeId, nodeId, distance, DirectionHint: DirectionHint.Arrive));
        bays.Add(new ParkingBay(bayId, nodeId, floor, x, y, primaryType, allowedTypes));

        bayIndex++;
    }

    private static (VehicleType Primary, VehicleType[] Allowed) ResolveBayVehicleTypes(int bayIndex) =>
        bayIndex switch
        {
            <= 4 => (VehicleType.Pcd, [VehicleType.Pcd]),
            <= 10 => (VehicleType.EV, [VehicleType.EV]),
            <= 16 => (VehicleType.SUV, [VehicleType.SUV, VehicleType.Standard]),
            <= 20 => (VehicleType.Moto, [VehicleType.Moto]),
            _ => (VehicleType.Standard, [VehicleType.Standard])
        };

    private static void AddDirectedSegment(
        List<GraphEdge> edges,
        List<GraphNode> nodes,
        string fromId,
        string toId,
        DirectionHint hint)
    {
        var from = nodes.First(n => n.Id == fromId);
        var to = nodes.First(n => n.Id == toId);
        var dist = Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2));

        edges.Add(new GraphEdge($"E-{fromId}-TO-{toId}", fromId, toId, dist, DirectionHint: hint));
    }

    private static void AddBidirectionalSegment(
        List<GraphEdge> edges,
        List<GraphNode> nodes,
        string aId,
        string bId,
        DirectionHint aToBHint,
        DirectionHint bToAHint)
    {
        AddDirectedSegment(edges, nodes, aId, bId, aToBHint);
        AddDirectedSegment(edges, nodes, bId, aId, bToAHint);
    }
}
