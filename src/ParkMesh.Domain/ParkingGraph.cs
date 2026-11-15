namespace ParkMesh.Domain;

public sealed class ParkingGraph
{
    private readonly Dictionary<string, GraphNode> _nodes;
    private readonly List<GraphEdge> _edges;
    private readonly Dictionary<string, IReadOnlyList<GraphEdge>> _adjacency;
    private readonly Dictionary<string, ParkingBay> _baysById;
    private readonly Dictionary<string, ParkingBay> _baysByNodeId;
    private readonly List<GraphNode> _gates;

    public ParkingGraph(
        IEnumerable<GraphNode> nodes,
        IEnumerable<GraphEdge> edges,
        IEnumerable<ParkingBay> bays)
    {
        _nodes = nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        _edges = edges.ToList();
        _baysById = bays.ToDictionary(b => b.BayId, StringComparer.OrdinalIgnoreCase);
        _baysByNodeId = _baysById.Values.ToDictionary(b => b.NodeId, StringComparer.OrdinalIgnoreCase);
        _gates = _nodes.Values.Where(n => n.Type == NodeType.Gate).ToList();

        var adj = new Dictionary<string, List<GraphEdge>>(StringComparer.OrdinalIgnoreCase);
        foreach (var nodeId in _nodes.Keys)
        {
            adj[nodeId] = [];
        }

        foreach (var edge in _edges)
        {
            if (!_nodes.ContainsKey(edge.FromNodeId))
                throw new InvalidOperationException($"Edge '{edge.Id}' references unknown source node '{edge.FromNodeId}'.");
            if (!_nodes.ContainsKey(edge.ToNodeId))
                throw new InvalidOperationException($"Edge '{edge.Id}' references unknown target node '{edge.ToNodeId}'.");

            adj[edge.FromNodeId].Add(edge);
        }

        _adjacency = adj.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<GraphEdge>)kvp.Value.AsReadOnly(),
            StringComparer.OrdinalIgnoreCase);

        MinFloor = _nodes.Values.Min(n => n.Floor);
        MaxFloor = _nodes.Values.Max(n => n.Floor);
    }

    public IReadOnlyDictionary<string, GraphNode> Nodes => _nodes;
    public IReadOnlyList<GraphEdge> Edges => _edges;
    public IReadOnlyDictionary<string, IReadOnlyList<GraphEdge>> Adjacency => _adjacency;
    public IReadOnlyDictionary<string, ParkingBay> BaysById => _baysById;
    public IReadOnlyDictionary<string, ParkingBay> BaysByNodeId => _baysByNodeId;
    public IReadOnlyList<GraphNode> Gates => _gates;
    public int MinFloor { get; }
    public int MaxFloor { get; }

    public bool TryGetNode(string nodeId, out GraphNode node) => _nodes.TryGetValue(nodeId, out node!);

    public bool TryGetBay(string bayId, out ParkingBay bay) => _baysById.TryGetValue(bayId, out bay!);

    public IReadOnlyList<GraphEdge> GetOutgoingEdges(string nodeId) =>
        _adjacency.TryGetValue(nodeId, out var edges) ? edges : Array.Empty<GraphEdge>();

    public ParkingGraph CloneTopology() =>
        new(
            _nodes.Values,
            _edges,
            _baysById.Values.Select(b => new ParkingBay(
                b.BayId,
                b.NodeId,
                b.Floor,
                b.X,
                b.Y,
                b.PrimaryType,
                b.AllowedTypes)));
}
