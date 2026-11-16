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
            switch (floor)
            {
                case 0:
                    BuildGroundFloorTopology(floor, nodes, edges, bays);
                    break;
                case 2:
                    BuildRooftopFloorTopology(floor, nodes, edges, bays);
                    break;
                default:
                    BuildStandardFloorTopology(floor, nodes, edges, bays);
                    break;
            }
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

    /// <summary>
    /// Builds Floor 0 (Ground Street Entry) with an open West concierge &amp; valet concourse,
    /// an East-Central boulevard drop-off island, and horizontal courier/express stalls.
    /// </summary>
    private static void BuildGroundFloorTopology(
        int floor,
        List<GraphNode> nodes,
        List<GraphEdge> edges,
        List<ParkingBay> bays)
    {
        var (northNodes, southNodes, wn, wc, ws, mc, en, ec, es) =
            BuildSharedDeckCorridors(floor, nodes, edges);

        var mE1 = $"F{floor}-I-M-E1";
        var mN1 = $"F{floor}-I-M-N1";
        var mS1 = $"F{floor}-I-M-S1";
        var eN1 = $"F{floor}-I-E-N1";
        var eS1 = $"F{floor}-I-E-S1";

        nodes.Add(new GraphNode(mE1, floor, 67, 30, NodeType.Intersection));
        nodes.Add(new GraphNode(mN1, floor, 50, 23, NodeType.Intersection));
        nodes.Add(new GraphNode(mS1, floor, 50, 37, NodeType.Intersection));
        nodes.Add(new GraphNode(eN1, floor, 90, 23, NodeType.Intersection));
        nodes.Add(new GraphNode(eS1, floor, 90, 37, NodeType.Intersection));

        // Central avenue (with East drop-off bay attachment node) and split N-S spines
        AddBidirectionalSegment(edges, nodes, wc, mc, DirectionHint.Straight, DirectionHint.Straight);
        AddBidirectionalSegment(edges, nodes, mc, mE1, DirectionHint.Straight, DirectionHint.Straight);
        AddBidirectionalSegment(edges, nodes, mE1, ec, DirectionHint.Straight, DirectionHint.Straight);

        AddBidirectionalChain(edges, nodes, [northNodes[2], mN1, mc, mS1, southNodes[2]]);
        AddBidirectionalChain(edges, nodes, [en, eN1, ec, eS1, es]);

        var bayIndex = 1;

        // 1-4: PCD accessible bays right off Gate-N1 / Gate-S1 beside the Ground Lobby
        AddBay(floor, ref bayIndex, northNodes[0], 17.5, 4, nodes, edges, bays);
        AddBay(floor, ref bayIndex, northNodes[0], 22.5, 4, nodes, edges, bays);
        AddBay(floor, ref bayIndex, southNodes[0], 17.5, 56, nodes, edges, bays);
        AddBay(floor, ref bayIndex, southNodes[0], 22.5, 56, nodes, edges, bays);

        // 5-10: EV bays along the North Express Sector (including F0-B09 off N-3)
        AddBay(floor, ref bayIndex, northNodes[1], 32.5, 4, nodes, edges, bays);
        AddBay(floor, ref bayIndex, northNodes[1], 37.5, 4, nodes, edges, bays);
        AddBay(floor, ref bayIndex, northNodes[2], 47.5, 16, nodes, edges, bays);
        AddBay(floor, ref bayIndex, northNodes[2], 52.5, 16, nodes, edges, bays);
        AddBay(floor, ref bayIndex, northNodes[2], 47.5, 4, nodes, edges, bays);
        AddBay(floor, ref bayIndex, northNodes[2], 52.5, 4, nodes, edges, bays);

        // 11-16: SUV bays in the North-East outer & inner sector
        AddBay(floor, ref bayIndex, northNodes[3], 62.5, 4, nodes, edges, bays);
        AddBay(floor, ref bayIndex, northNodes[3], 67.5, 4, nodes, edges, bays);
        AddBay(floor, ref bayIndex, northNodes[4], 77.5, 4, nodes, edges, bays);
        AddBay(floor, ref bayIndex, northNodes[4], 82.5, 4, nodes, edges, bays);
        AddBay(floor, ref bayIndex, northNodes[3], 62.5, 16, nodes, edges, bays);
        AddBay(floor, ref bayIndex, northNodes[3], 67.5, 16, nodes, edges, bays);

        // 17-20: Compact Moto horizontal courier bays along Central & East spines
        AddBay(floor, ref bayIndex, mN1, 44.0, 23, nodes, edges, bays);
        AddBay(floor, ref bayIndex, mS1, 44.0, 37, nodes, edges, bays);
        AddBay(floor, ref bayIndex, eN1, 84.0, 23, nodes, edges, bays);
        AddBay(floor, ref bayIndex, eS1, 84.0, 37, nodes, edges, bays);

        // 21-40: Standard bays in East inner sector, Central VIP curb island, & South sector
        AddBay(floor, ref bayIndex, northNodes[4], 77.5, 16, nodes, edges, bays);
        AddBay(floor, ref bayIndex, northNodes[4], 82.5, 16, nodes, edges, bays);

        AddBay(floor, ref bayIndex, mE1, 64.5, 24.0, nodes, edges, bays);
        AddBay(floor, ref bayIndex, mE1, 69.5, 24.0, nodes, edges, bays);
        AddBay(floor, ref bayIndex, mE1, 64.5, 36.0, nodes, edges, bays);
        AddBay(floor, ref bayIndex, mE1, 69.5, 36.0, nodes, edges, bays);

        AddBay(floor, ref bayIndex, southNodes[2], 47.5, 44, nodes, edges, bays);
        AddBay(floor, ref bayIndex, southNodes[2], 52.5, 44, nodes, edges, bays);
        AddBay(floor, ref bayIndex, southNodes[3], 62.5, 44, nodes, edges, bays);
        AddBay(floor, ref bayIndex, southNodes[3], 67.5, 44, nodes, edges, bays);
        AddBay(floor, ref bayIndex, southNodes[4], 77.5, 44, nodes, edges, bays);
        AddBay(floor, ref bayIndex, southNodes[4], 82.5, 44, nodes, edges, bays);

        AddBay(floor, ref bayIndex, southNodes[1], 32.5, 56, nodes, edges, bays);
        AddBay(floor, ref bayIndex, southNodes[1], 37.5, 56, nodes, edges, bays);
        AddBay(floor, ref bayIndex, southNodes[2], 47.5, 56, nodes, edges, bays);
        AddBay(floor, ref bayIndex, southNodes[2], 52.5, 56, nodes, edges, bays);
        AddBay(floor, ref bayIndex, southNodes[3], 62.5, 56, nodes, edges, bays);
        AddBay(floor, ref bayIndex, southNodes[3], 67.5, 56, nodes, edges, bays);
        AddBay(floor, ref bayIndex, southNodes[4], 77.5, 56, nodes, edges, bays);
        AddBay(floor, ref bayIndex, southNodes[4], 82.5, 56, nodes, edges, bays);
    }

    private static void BuildStandardFloorTopology(
        int floor,
        List<GraphNode> nodes,
        List<GraphEdge> edges,
        List<ParkingBay> bays)
    {
        var (northNodes, southNodes, _, wc, _, mc, en, ec, es) =
            BuildSharedDeckCorridors(floor, nodes, edges);

        AddBidirectionalSegment(edges, nodes, wc, mc, DirectionHint.Straight, DirectionHint.Straight);
        AddBidirectionalSegment(edges, nodes, mc, ec, DirectionHint.Straight, DirectionHint.Straight);

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

    private static (
        List<string> NorthNodes,
        List<string> SouthNodes,
        string Wn,
        string Wc,
        string Ws,
        string Mc,
        string En,
        string Ec,
        string Es)
        BuildSharedDeckCorridors(int floor, List<GraphNode> nodes, List<GraphEdge> edges)
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

        AddBidirectionalSegment(edges, nodes, wn, wc, DirectionHint.Right, DirectionHint.Left);
        AddBidirectionalSegment(edges, nodes, wc, ws, DirectionHint.Straight, DirectionHint.Straight);

        return (northNodes, southNodes, wn, wc, ws, mc, en, ec, es);
    }

    /// <summary>
    /// Builds Floor 2 (Rooftop Sky Deck) with a perimeter circulation loop, twin interior
    /// wing avenues around an open central skylight atrium void, and horizontal + perimeter bays.
    /// </summary>
    private static void BuildRooftopFloorTopology(
        int floor,
        List<GraphNode> nodes,
        List<GraphEdge> edges,
        List<ParkingBay> bays)
    {
        var westSpine = AddVerticalAxisNodes(floor, "W", 10, nodes, endLedSuffix: "W");
        var westAtrium = AddVerticalAxisNodes(floor, "WA", 28, nodes, endLedSuffix: "WA", includeCenterLed: false);
        var eastAtrium = AddVerticalAxisNodes(floor, "EA", 72, nodes, endLedSuffix: "EA", includeCenterLed: false);
        var eastSpine = AddVerticalAxisNodes(floor, "E", 90, nodes, endLedSuffix: "E");

        AddBidirectionalChain(edges, nodes, westSpine);
        AddBidirectionalChain(edges, nodes, westAtrium);
        AddBidirectionalChain(edges, nodes, eastAtrium);
        AddBidirectionalChain(edges, nodes, eastSpine);

        // Wing cross-connectors flanking the car-free central skylight atrium
        AddBidirectionalSegment(edges, nodes, $"F{floor}-I-W-C", $"F{floor}-I-WA-C", DirectionHint.Straight, DirectionHint.Straight);
        AddBidirectionalSegment(edges, nodes, $"F{floor}-I-E-C", $"F{floor}-I-EA-C", DirectionHint.Straight, DirectionHint.Straight);

        // North boulevard attachment intersections (y = 10)
        var nw1 = $"F{floor}-I-NW-1";
        var nw2 = $"F{floor}-I-NW-2";
        var nc1 = $"F{floor}-I-NC-1";
        var nc2 = $"F{floor}-I-NC-2";
        var nc3 = $"F{floor}-I-NC-3";
        var nc4 = $"F{floor}-I-NC-4";
        var ne1 = $"F{floor}-I-NE-1";
        var ne2 = $"F{floor}-I-NE-2";

        nodes.Add(new GraphNode(nw1, floor, 17.75, 10, NodeType.Intersection));
        nodes.Add(new GraphNode(nw2, floor, 24.5, 10, NodeType.Intersection));
        nodes.Add(new GraphNode(nc1, floor, 39.75, 10, NodeType.Intersection));
        nodes.Add(new GraphNode(nc2, floor, 46.5, 10, NodeType.Intersection));
        nodes.Add(new GraphNode(nc3, floor, 53.5, 10, NodeType.Intersection));
        nodes.Add(new GraphNode(nc4, floor, 60.25, 10, NodeType.Intersection));
        nodes.Add(new GraphNode(ne1, floor, 75.5, 10, NodeType.Intersection));
        nodes.Add(new GraphNode(ne2, floor, 82.25, 10, NodeType.Intersection));

        // Counter-clockwise one-way North boulevard (East -> West)
        AddDirectedChain(edges, nodes, [
            $"F{floor}-I-E-N", ne2, ne1, $"F{floor}-I-EA-N",
            nc4, nc3, nc2, nc1,
            $"F{floor}-I-WA-N", nw2, nw1, $"F{floor}-I-W-N"
        ]);

        // South boulevard attachment intersections (y = 50)
        var sw1 = $"F{floor}-I-SW-1";
        var sw2 = $"F{floor}-I-SW-2";
        var sc1 = $"F{floor}-I-SC-1";
        var sc2 = $"F{floor}-I-SC-2";
        var sc3 = $"F{floor}-I-SC-3";
        var sc4 = $"F{floor}-I-SC-4";
        var se1 = $"F{floor}-I-SE-1";
        var se2 = $"F{floor}-I-SE-2";

        nodes.Add(new GraphNode(sw1, floor, 17.75, 50, NodeType.Intersection));
        nodes.Add(new GraphNode(sw2, floor, 24.5, 50, NodeType.Intersection));
        nodes.Add(new GraphNode(sc1, floor, 39.75, 50, NodeType.Intersection));
        nodes.Add(new GraphNode(sc2, floor, 46.5, 50, NodeType.Intersection));
        nodes.Add(new GraphNode(sc3, floor, 53.5, 50, NodeType.Intersection));
        nodes.Add(new GraphNode(sc4, floor, 60.25, 50, NodeType.Intersection));
        nodes.Add(new GraphNode(se1, floor, 75.5, 50, NodeType.Intersection));
        nodes.Add(new GraphNode(se2, floor, 82.25, 50, NodeType.Intersection));

        // Counter-clockwise one-way South boulevard (West -> East)
        AddDirectedChain(edges, nodes, [
            $"F{floor}-I-W-S", sw1, sw2, $"F{floor}-I-WA-S",
            sc1, sc2, sc3, sc4,
            $"F{floor}-I-EA-S", se1, se2, $"F{floor}-I-E-S"
        ]);

        var bayIndex = 1;

        // 1-4: PCD accessible bays right by the North & South glass skybridges
        AddBay(floor, ref bayIndex, nc2, 46.5, 4, nodes, edges, bays);
        AddBay(floor, ref bayIndex, nc3, 53.5, 4, nodes, edges, bays);
        AddBay(floor, ref bayIndex, sc2, 46.5, 56, nodes, edges, bays);
        AddBay(floor, ref bayIndex, sc3, 53.5, 56, nodes, edges, bays);

        // 5-10: EV bays sheltered under the North & South solar pergola canopies
        AddBay(floor, ref bayIndex, nc1, 37.5, 4, nodes, edges, bays);
        AddBay(floor, ref bayIndex, nc1, 42.0, 4, nodes, edges, bays);
        AddBay(floor, ref bayIndex, nc4, 58.0, 4, nodes, edges, bays);
        AddBay(floor, ref bayIndex, nc4, 62.5, 4, nodes, edges, bays);
        AddBay(floor, ref bayIndex, sc1, 42.0, 56, nodes, edges, bays);
        AddBay(floor, ref bayIndex, sc4, 58.0, 56, nodes, edges, bays);

        // 11-16: SUV bays in the horizontal East & West rooftop wing bays
        AddBay(floor, ref bayIndex, $"F{floor}-I-W-N1", 16.0, 18, nodes, edges, bays);
        AddBay(floor, ref bayIndex, $"F{floor}-I-W-N2", 16.0, 24, nodes, edges, bays);
        AddBay(floor, ref bayIndex, $"F{floor}-I-W-S1", 16.0, 36, nodes, edges, bays);
        AddBay(floor, ref bayIndex, $"F{floor}-I-W-S2", 16.0, 42, nodes, edges, bays);
        AddBay(floor, ref bayIndex, $"F{floor}-I-E-N1", 84.0, 18, nodes, edges, bays);
        AddBay(floor, ref bayIndex, $"F{floor}-I-E-N2", 84.0, 24, nodes, edges, bays);

        // 17-20: Compact Moto bays at the four perimeter corners
        AddBay(floor, ref bayIndex, nw1, 15.5, 4, nodes, edges, bays);
        AddBay(floor, ref bayIndex, ne2, 84.5, 4, nodes, edges, bays);
        AddBay(floor, ref bayIndex, sw1, 15.5, 56, nodes, edges, bays);
        AddBay(floor, ref bayIndex, se2, 84.5, 56, nodes, edges, bays);

        // 21-40: Standard bays filling inner atrium wings and outer perimeter banks
        AddBay(floor, ref bayIndex, nw1, 20.0, 4, nodes, edges, bays);
        AddBay(floor, ref bayIndex, nw2, 24.5, 4, nodes, edges, bays);
        AddBay(floor, ref bayIndex, ne1, 75.5, 4, nodes, edges, bays);
        AddBay(floor, ref bayIndex, ne2, 80.0, 4, nodes, edges, bays);

        AddBay(floor, ref bayIndex, $"F{floor}-I-WA-N1", 22.0, 18, nodes, edges, bays);
        AddBay(floor, ref bayIndex, $"F{floor}-I-WA-N2", 22.0, 24, nodes, edges, bays);
        AddBay(floor, ref bayIndex, $"F{floor}-I-WA-S1", 22.0, 36, nodes, edges, bays);
        AddBay(floor, ref bayIndex, $"F{floor}-I-WA-S2", 22.0, 42, nodes, edges, bays);

        AddBay(floor, ref bayIndex, $"F{floor}-I-EA-N1", 78.0, 18, nodes, edges, bays);
        AddBay(floor, ref bayIndex, $"F{floor}-I-EA-N2", 78.0, 24, nodes, edges, bays);
        AddBay(floor, ref bayIndex, $"F{floor}-I-EA-S1", 78.0, 36, nodes, edges, bays);
        AddBay(floor, ref bayIndex, $"F{floor}-I-EA-S2", 78.0, 42, nodes, edges, bays);

        AddBay(floor, ref bayIndex, $"F{floor}-I-E-S1", 84.0, 36, nodes, edges, bays);
        AddBay(floor, ref bayIndex, $"F{floor}-I-E-S2", 84.0, 42, nodes, edges, bays);

        AddBay(floor, ref bayIndex, sw1, 20.0, 56, nodes, edges, bays);
        AddBay(floor, ref bayIndex, sw2, 24.5, 56, nodes, edges, bays);
        AddBay(floor, ref bayIndex, sc1, 37.5, 56, nodes, edges, bays);
        AddBay(floor, ref bayIndex, sc4, 62.5, 56, nodes, edges, bays);
        AddBay(floor, ref bayIndex, se1, 75.5, 56, nodes, edges, bays);
        AddBay(floor, ref bayIndex, se2, 80.0, 56, nodes, edges, bays);
    }

    private static string[] AddVerticalAxisNodes(
        int floor,
        string prefix,
        double x,
        List<GraphNode> nodes,
        string endLedSuffix,
        bool includeCenterLed = true)
    {
        var wn = $"F{floor}-I-{prefix}-N";
        var n1 = $"F{floor}-I-{prefix}-N1";
        var n2 = $"F{floor}-I-{prefix}-N2";
        var c = $"F{floor}-I-{prefix}-C";
        var s1 = $"F{floor}-I-{prefix}-S1";
        var s2 = $"F{floor}-I-{prefix}-S2";
        var ws = $"F{floor}-I-{prefix}-S";

        nodes.Add(new GraphNode(wn, floor, x, 10, NodeType.Intersection, $"LED-F{floor}-{endLedSuffix}N"));
        nodes.Add(new GraphNode(n1, floor, x, 18, NodeType.Intersection));
        nodes.Add(new GraphNode(n2, floor, x, 24, NodeType.Intersection));
        nodes.Add(new GraphNode(c, floor, x, 30, NodeType.Intersection, includeCenterLed ? $"LED-F{floor}-{endLedSuffix}C" : null));
        nodes.Add(new GraphNode(s1, floor, x, 36, NodeType.Intersection));
        nodes.Add(new GraphNode(s2, floor, x, 42, NodeType.Intersection));
        nodes.Add(new GraphNode(ws, floor, x, 50, NodeType.Intersection, $"LED-F{floor}-{endLedSuffix}S"));

        return [wn, n1, n2, c, s1, s2, ws];
    }

    private static void AddDirectedChain(List<GraphEdge> edges, List<GraphNode> nodes, string[] chain)
    {
        for (var i = 0; i < chain.Length - 1; i++)
        {
            AddDirectedSegment(edges, nodes, chain[i], chain[i + 1], DirectionHint.Straight);
        }
    }

    private static void AddBidirectionalChain(List<GraphEdge> edges, List<GraphNode> nodes, string[] chain)
    {
        for (var i = 0; i < chain.Length - 1; i++)
        {
            var forward = i == 0 ? DirectionHint.Right : DirectionHint.Straight;
            var backward = i == 0 ? DirectionHint.Left : DirectionHint.Straight;
            AddBidirectionalSegment(edges, nodes, chain[i], chain[i + 1], forward, backward);
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
