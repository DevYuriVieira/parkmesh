using System.Collections.Concurrent;

namespace ParkMesh.Application.Abstractions;

public interface IEdgeCongestionTracker
{
    double GetPenalty(string edgeId);
    void AddRouteLoad(IEnumerable<string> edgeIds, double weightPerVehicle = 6.0);
    void RemoveRouteLoad(IEnumerable<string> edgeIds, double weightPerVehicle = 6.0);
    IReadOnlyDictionary<string, double> Snapshot();
}

public sealed class InMemoryEdgeCongestionTracker : IEdgeCongestionTracker
{
    private readonly ConcurrentDictionary<string, double> _penalties = new(StringComparer.OrdinalIgnoreCase);

    public double GetPenalty(string edgeId) =>
        _penalties.TryGetValue(edgeId, out var penalty) ? penalty : 0.0;

    public void AddRouteLoad(IEnumerable<string> edgeIds, double weightPerVehicle = 6.0)
    {
        foreach (var edgeId in edgeIds)
        {
            _penalties.AddOrUpdate(edgeId, weightPerVehicle, (_, current) => current + weightPerVehicle);
        }
    }

    public void RemoveRouteLoad(IEnumerable<string> edgeIds, double weightPerVehicle = 6.0)
    {
        foreach (var edgeId in edgeIds)
        {
            _penalties.AddOrUpdate(
                edgeId,
                0.0,
                (_, current) => Math.Max(0.0, current - weightPerVehicle));
        }
    }

    public IReadOnlyDictionary<string, double> Snapshot() =>
        _penalties.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
}
