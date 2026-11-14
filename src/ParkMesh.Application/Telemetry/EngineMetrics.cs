using System.Diagnostics;
using System.Diagnostics.Metrics;
using ParkMesh.Domain;

namespace ParkMesh.Application.Telemetry;

public sealed record LatencyPercentiles(
    double P50Ms,
    double P95Ms,
    double P99Ms,
    int SampleCount);

public sealed record EngineMetricsSnapshot(
    int TotalBays,
    int FreeBays,
    int LeasedBays,
    int OccupiedBays,
    int ActiveSessions,
    long AllocationsTotal,
    long ContentionRetriesTotal,
    long ReroutesTotal,
    LatencyPercentiles AStarLatency,
    LatencyPercentiles RedisLeaseLatency,
    TelemetryMetricsSnapshot Telemetry,
    long LeasesExpiredTotal = 0,
    int OutOfServiceBays = 0);

public interface IEngineMetricsRecorder
{
    void RecordAStarLatency(double durationMs);
    void RecordRedisLeaseLatency(double durationMs);
    void RecordAllocation();
    void RecordContentionRetry();
    void RecordReroute();
    void RecordLeaseExpired();
    EngineMetricsSnapshot BuildSnapshot(ParkingGraph graph, int activeSessions, TelemetryMetricsSnapshot telemetry);
}

public sealed class SlidingLatencyWindow
{
    private readonly double[] _samples;
    private readonly Lock _lock = new();
    private int _index;
    private int _count;

    public SlidingLatencyWindow(int capacity = 1024)
    {
        _samples = new double[Math.Max(16, capacity)];
    }

    public void Add(double durationMs)
    {
        lock (_lock)
        {
            _samples[_index] = durationMs;
            _index = (_index + 1) % _samples.Length;
            if (_count < _samples.Length)
                _count++;
        }
    }

    public LatencyPercentiles ComputePercentiles()
    {
        double[] copy;
        lock (_lock)
        {
            if (_count == 0)
                return new LatencyPercentiles(0, 0, 0, 0);

            copy = new double[_count];
            Array.Copy(_samples, copy, _count);
        }

        Array.Sort(copy);
        return new LatencyPercentiles(
            Math.Round(Percentile(copy, 0.50), 3),
            Math.Round(Percentile(copy, 0.95), 3),
            Math.Round(Percentile(copy, 0.99), 3),
            copy.Length);
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 1)
            return sorted[0];

        var rank = p * (sorted.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        var weight = rank - lower;
        return sorted[lower] + (sorted[upper] - sorted[lower]) * weight;
    }
}

public sealed class EngineMetricsRecorder : IEngineMetricsRecorder, IDisposable
{
    public const string MeterName = "ParkMesh.Engine";
    public const string ActivitySourceName = "ParkMesh.Engine";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, "1.0.0");

    private readonly Meter _meter = new(MeterName, "1.0.0");
    private readonly Histogram<double> _astarHistogram;
    private readonly Histogram<double> _redisLeaseHistogram;
    private readonly Counter<long> _allocationsCounter;
    private readonly Counter<long> _contentionCounter;
    private readonly Counter<long> _reroutesCounter;
    private readonly Counter<long> _leasesExpiredCounter;

    private readonly SlidingLatencyWindow _astarWindow = new();
    private readonly SlidingLatencyWindow _redisWindow = new();
    private long _allocationsTotal;
    private long _contentionRetriesTotal;
    private long _reroutesTotal;
    private long _leasesExpiredTotal;

    public EngineMetricsRecorder()
    {
        _astarHistogram = _meter.CreateHistogram<double>("parkmesh.routing.astar.duration", "ms");
        _redisLeaseHistogram = _meter.CreateHistogram<double>("parkmesh.lease.redis.duration", "ms");
        _allocationsCounter = _meter.CreateCounter<long>("parkmesh.allocations.total");
        _contentionCounter = _meter.CreateCounter<long>("parkmesh.allocations.contention");
        _reroutesCounter = _meter.CreateCounter<long>("parkmesh.reroutes.total");
        _leasesExpiredCounter = _meter.CreateCounter<long>("parkmesh.leases.expired.total");
    }

    public void RecordAStarLatency(double durationMs)
    {
        _astarWindow.Add(durationMs);
        _astarHistogram.Record(durationMs);
    }

    public void RecordRedisLeaseLatency(double durationMs)
    {
        _redisWindow.Add(durationMs);
        _redisLeaseHistogram.Record(durationMs);
    }

    public void RecordAllocation()
    {
        Interlocked.Increment(ref _allocationsTotal);
        _allocationsCounter.Add(1);
    }

    public void RecordContentionRetry()
    {
        Interlocked.Increment(ref _contentionRetriesTotal);
        _contentionCounter.Add(1);
    }

    public void RecordReroute()
    {
        Interlocked.Increment(ref _reroutesTotal);
        _reroutesCounter.Add(1);
    }

    public void RecordLeaseExpired()
    {
        Interlocked.Increment(ref _leasesExpiredTotal);
        _leasesExpiredCounter.Add(1);
    }

    public EngineMetricsSnapshot BuildSnapshot(
        ParkingGraph graph,
        int activeSessions,
        TelemetryMetricsSnapshot telemetry)
    {
        var free = 0;
        var leased = 0;
        var occupied = 0;
        var outOfService = 0;

        foreach (var bay in graph.BaysById.Values)
        {
            switch (bay.Status)
            {
                case BayStatus.Free:
                    free++;
                    break;
                case BayStatus.Leased:
                    leased++;
                    break;
                case BayStatus.Occupied:
                    occupied++;
                    break;
                case BayStatus.OutOfService:
                    outOfService++;
                    break;
            }
        }

        return new EngineMetricsSnapshot(
            graph.BaysById.Count,
            free,
            leased,
            occupied,
            activeSessions,
            Interlocked.Read(ref _allocationsTotal),
            Interlocked.Read(ref _contentionRetriesTotal),
            Interlocked.Read(ref _reroutesTotal),
            _astarWindow.ComputePercentiles(),
            _redisWindow.ComputePercentiles(),
            telemetry,
            Interlocked.Read(ref _leasesExpiredTotal),
            outOfService);
    }

    public void Dispose() => _meter.Dispose();
}

public sealed class NullEngineMetricsRecorder : IEngineMetricsRecorder
{
    public static readonly NullEngineMetricsRecorder Instance = new();

    public void RecordAStarLatency(double durationMs) { }
    public void RecordRedisLeaseLatency(double durationMs) { }
    public void RecordAllocation() { }
    public void RecordContentionRetry() { }
    public void RecordReroute() { }
    public void RecordLeaseExpired() { }

    public EngineMetricsSnapshot BuildSnapshot(
        ParkingGraph graph,
        int activeSessions,
        TelemetryMetricsSnapshot telemetry) =>
        new(
            graph.BaysById.Count,
            graph.BaysById.Values.Count(b => b.Status == BayStatus.Free),
            graph.BaysById.Values.Count(b => b.Status == BayStatus.Leased),
            graph.BaysById.Values.Count(b => b.Status == BayStatus.Occupied),
            activeSessions,
            0,
            0,
            0,
            new LatencyPercentiles(0, 0, 0, 0),
            new LatencyPercentiles(0, 0, 0, 0),
            telemetry);
}
