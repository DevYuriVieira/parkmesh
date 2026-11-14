using ParkMesh.Domain;

namespace ParkMesh.Application.Routing;

public interface IPathfinder
{
    ParkingRoute? FindShortestPath(
        ParkingGraph graph,
        string startNodeId,
        string targetBayId,
        IReadOnlyDictionary<string, double>? edgeCongestion = null);

    IReadOnlyList<ParkingRoute> FindCandidateBayRoutes(
        ParkingGraph graph,
        string startNodeId,
        VehicleType vehicleType,
        IReadOnlyDictionary<string, double>? edgeCongestion = null,
        int maxCandidates = 10);
}
