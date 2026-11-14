namespace ParkMesh.Domain;

public sealed record RouteStep(
    string EdgeId,
    string FromNodeId,
    string ToNodeId,
    int Floor,
    DirectionHint Direction,
    double Cost);

public sealed record LedDirective(
    string PanelId,
    int Floor,
    DirectionHint Direction);

public sealed record ParkingRoute(
    string TargetBayId,
    string TargetNodeId,
    int TargetFloor,
    double TotalCost,
    IReadOnlyList<string> NodeIds,
    IReadOnlyList<RouteStep> Steps,
    IReadOnlyList<LedDirective> LedDirectives);
