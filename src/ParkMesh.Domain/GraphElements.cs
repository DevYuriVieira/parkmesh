namespace ParkMesh.Domain;

public sealed record GraphNode(
    string Id,
    int Floor,
    double X,
    double Y,
    NodeType Type,
    string? LedPanelId = null);

public sealed record GraphEdge(
    string Id,
    string FromNodeId,
    string ToNodeId,
    double BaseDistance,
    bool IsRamp = false,
    double RampPenalty = 1.0,
    DirectionHint DirectionHint = DirectionHint.Straight)
{
    public double EffectiveBaseCost => BaseDistance * RampPenalty;
}
