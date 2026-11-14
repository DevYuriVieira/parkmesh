using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using ParkMesh.Application.Abstractions;
using ParkMesh.Application.Allocation;
using ParkMesh.Application.Routing;
using ParkMesh.Application.Telemetry;
using ParkMesh.Domain;
using ParkMesh.Infrastructure.Redis;

namespace ParkMesh.Benchmarks;

[MemoryDiagnoser]
public class RoutingAndTelemetryBenchmarks
{
    private ParkingGraph _graph = null!;
    private AStarPathfinder _pathfinder = null!;
    private IReadOnlyDictionary<string, double> _congestionSnapshot = null!;
    private ParkingAllocationService _allocationService = null!;
    private SensorReadingFilter _filter = null!;
    private SlidingLatencyWindow _latencyWindow = null!;
    private long _seq;

    [GlobalSetup]
    public void Setup()
    {
        _graph = StandardTopologyFactory.CreateStandardGarage();
        _pathfinder = new AStarPathfinder();

        var congestion = new InMemoryEdgeCongestionTracker();
        var seedEdges = _graph.Edges.Take(12).Select(e => e.Id).ToArray();
        congestion.AddRouteLoad(seedEdges);
        congestion.AddRouteLoad(seedEdges);
        _congestionSnapshot = congestion.Snapshot();

        _allocationService = new ParkingAllocationService(
            StandardTopologyFactory.CreateStandardGarage(),
            new AStarPathfinder(),
            new InMemoryBayLeaseManager(),
            new InMemoryEdgeCongestionTracker(),
            NullLogger<ParkingAllocationService>.Instance);

        _filter = new SensorReadingFilter();

        _latencyWindow = new SlidingLatencyWindow(1024);
        for (var i = 0; i < 1024; i++)
        {
            _latencyWindow.Add(0.15 + (i % 97) * 0.03);
        }
    }

    [Benchmark]
    public IReadOnlyList<ParkingRoute> AStar_Find15Candidates_Standard() =>
        _pathfinder.FindCandidateBayRoutes(_graph, "GATE-N1", VehicleType.Standard, _congestionSnapshot, 15);

    [Benchmark]
    public IReadOnlyList<ParkingRoute> AStar_Find15Candidates_EV() =>
        _pathfinder.FindCandidateBayRoutes(_graph, "GATE-S1", VehicleType.EV, _congestionSnapshot, 15);

    [Benchmark]
    public async Task AllocateAndRelease_InMemory()
    {
        var sessionId = $"BM-{++_seq}";
        var allocated = await _allocationService.AllocateAsync(
            new AllocateSpotRequest("GATE-N1", sessionId, VehicleType.Standard, TimeSpan.FromMinutes(1)));

        if (allocated.Succeeded && allocated.Session is not null)
        {
            await _allocationService.ReleaseLeaseAsync(
                allocated.Session.AssignedBayId,
                allocated.Session.FencingToken);
        }
    }

    [Benchmark]
    public SensorFilterOutcome TelemetryFilter_EvaluateReading()
    {
        var nextSeq = Interlocked.Increment(ref _seq);
        return _filter.Evaluate(new BaySensorReading("F0-B01", false, 240.0, nextSeq, DateTimeOffset.UtcNow));
    }

    [Benchmark]
    public LatencyPercentiles SlidingWindow_Compute1024Percentiles() =>
        _latencyWindow.ComputePercentiles();
}
