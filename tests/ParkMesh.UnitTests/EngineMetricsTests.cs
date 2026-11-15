using ParkMesh.Application.Telemetry;
using ParkMesh.Domain;

namespace ParkMesh.UnitTests;

public sealed class EngineMetricsTests
{
    [Fact]
    public void SlidingLatencyWindow_EmptyWindow_ReturnsZeros()
    {
        var window = new SlidingLatencyWindow(32);

        var percentiles = window.ComputePercentiles();

        Assert.Equal(0, percentiles.SampleCount);
        Assert.Equal(0, percentiles.P50Ms);
        Assert.Equal(0, percentiles.P95Ms);
        Assert.Equal(0, percentiles.P99Ms);
    }

    [Fact]
    public void SlidingLatencyWindow_ComputesLinearlyInterpolatedPercentiles()
    {
        var window = new SlidingLatencyWindow(32);
        for (var i = 1; i <= 100; i++)
            window.Add(i);

        var percentiles = window.ComputePercentiles();

        Assert.Equal(32, percentiles.SampleCount);
        Assert.Equal(84.5, percentiles.P50Ms);
        Assert.InRange(percentiles.P95Ms, 98.4, 98.6);
        Assert.InRange(percentiles.P99Ms, 99.6, 99.8);
    }

    [Fact]
    public void SlidingLatencyWindow_ClampsMinimumCapacityToSixteen()
    {
        var window = new SlidingLatencyWindow(4);
        for (var i = 1; i <= 20; i++)
            window.Add(i * 2.0);

        var percentiles = window.ComputePercentiles();

        Assert.Equal(16, percentiles.SampleCount);
        Assert.Equal(25.0, percentiles.P50Ms);
        Assert.True(percentiles.P99Ms >= percentiles.P95Ms);
        Assert.True(percentiles.P95Ms >= percentiles.P50Ms);
    }

    [Fact]
    public void EngineMetricsRecorder_BuildSnapshot_AggregatesBaysAndCounters()
    {
        var graph = StandardTopologyFactory.CreateStandardGarage();
        var bays = graph.BaysById.Values.OrderBy(b => b.BayId, StringComparer.OrdinalIgnoreCase).Take(3).ToArray();
        Assert.True(bays[0].TrySetLeased("S-1", 1, DateTimeOffset.UtcNow.AddMinutes(5)));
        bays[1].MarkOccupied("INTRUDER-1");

        using var recorder = new EngineMetricsRecorder();
        recorder.RecordAStarLatency(0.45);
        recorder.RecordAStarLatency(1.25);
        recorder.RecordRedisLeaseLatency(2.10);
        recorder.RecordAllocation();
        recorder.RecordAllocation();
        recorder.RecordContentionRetry();
        recorder.RecordReroute();

        var telemetry = new TelemetryMetricsSnapshot(12, 10, 3, 7, 1);
        var snapshot = recorder.BuildSnapshot(graph, activeSessions: 1, telemetry);

        Assert.Equal(120, snapshot.TotalBays);
        Assert.Equal(118, snapshot.FreeBays);
        Assert.Equal(1, snapshot.LeasedBays);
        Assert.Equal(1, snapshot.OccupiedBays);
        Assert.Equal(1, snapshot.ActiveSessions);
        Assert.Equal(2, snapshot.AllocationsTotal);
        Assert.Equal(1, snapshot.ContentionRetriesTotal);
        Assert.Equal(1, snapshot.ReroutesTotal);
        Assert.Equal(2, snapshot.AStarLatency.SampleCount);
        Assert.Equal(1, snapshot.RedisLeaseLatency.SampleCount);
        Assert.Equal(telemetry, snapshot.Telemetry);
    }

    [Fact]
    public void SlidingLatencyWindow_SingleSample_AndRecorder_CountsOutOfServiceAndExpiredLeases()
    {
        var window = new SlidingLatencyWindow(32);
        window.Add(12.345);
        var p = window.ComputePercentiles();

        Assert.Equal(1, p.SampleCount);
        Assert.Equal(12.345, p.P50Ms);
        Assert.Equal(12.345, p.P95Ms);
        Assert.Equal(12.345, p.P99Ms);

        var graph = StandardTopologyFactory.CreateStandardGarage();
        graph.BaysById["F0-B01"].MarkOutOfService();
        graph.BaysById["F0-B02"].MarkOutOfService();

        using var recorder = new EngineMetricsRecorder();
        recorder.RecordLeaseExpired();
        recorder.RecordLeaseExpired();
        recorder.RecordLeaseExpired();

        var snapshot = recorder.BuildSnapshot(graph, activeSessions: 0, new TelemetryMetricsSnapshot(0, 0, 0, 0, 0));

        Assert.Equal(2, snapshot.OutOfServiceBays);
        Assert.Equal(118, snapshot.FreeBays);
        Assert.Equal(3, snapshot.LeasesExpiredTotal);
    }
}
