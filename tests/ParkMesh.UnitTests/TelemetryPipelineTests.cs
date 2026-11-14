using Microsoft.Extensions.Logging.Abstractions;
using ParkMesh.Application.Abstractions;
using ParkMesh.Application.Allocation;
using ParkMesh.Application.Routing;
using ParkMesh.Application.Telemetry;
using ParkMesh.Domain;
using ParkMesh.Infrastructure.Redis;

namespace ParkMesh.UnitTests;

public sealed class TelemetryPipelineTests
{
    [Fact]
    public void SensorReadingFilter_RejectsDuplicateAndOutOfOrderSequences()
    {
        var filter = new SensorReadingFilter(new SensorDebounceOptions
        {
            MinConsecutiveReadings = 2,
            StabilityWindowMs = 1500
        });

        var t0 = DateTimeOffset.UtcNow;
        Assert.Equal(SensorFilterOutcome.Debouncing, filter.Evaluate(new BaySensorReading("F0-B21", true, 40, 10, t0)));
        Assert.Equal(SensorFilterOutcome.StaleSequence, filter.Evaluate(new BaySensorReading("F0-B21", true, 40, 10, t0.AddSeconds(1))));
        Assert.Equal(SensorFilterOutcome.StaleSequence, filter.Evaluate(new BaySensorReading("F0-B21", true, 40, 9, t0.AddSeconds(2))));
    }

    [Fact]
    public void SensorReadingFilter_IgnoresPedestrianGlitch_AndPromotesStableTransition()
    {
        var filter = new SensorReadingFilter(new SensorDebounceOptions
        {
            MinConsecutiveReadings = 2,
            StabilityWindowMs = 1500
        });

        var t0 = DateTimeOffset.UtcNow;
        Assert.Equal(SensorFilterOutcome.Debouncing, filter.Evaluate(new BaySensorReading("F0-B21", true, 65, 1, t0)));
        Assert.Equal(SensorFilterOutcome.Unchanged, filter.Evaluate(new BaySensorReading("F0-B21", false, 210, 2, t0.AddMilliseconds(400))));

        Assert.Equal(SensorFilterOutcome.Debouncing, filter.Evaluate(new BaySensorReading("F0-B21", true, 42, 3, t0.AddSeconds(2))));
        Assert.Equal(SensorFilterOutcome.Debouncing, filter.Evaluate(new BaySensorReading("F0-B21", true, 41, 4, t0.AddSeconds(3))));
        Assert.Equal(SensorFilterOutcome.ConfirmedTransition, filter.Evaluate(new BaySensorReading("F0-B21", true, 41, 5, t0.AddSeconds(4))));
    }

    [Fact]
    public async Task TelemetryIngestionChannel_DropsOldestUnderBurst_WithoutBlocking()
    {
        var channel = new TelemetryIngestionChannel(new SensorDebounceOptions { ChannelCapacity = 64 });
        var t0 = DateTimeOffset.UtcNow;

        for (var i = 1; i <= 200; i++)
        {
            Assert.True(channel.TryEnqueue(new BaySensorReading("F0-B21", true, 45, i, t0.AddMilliseconds(i))));
        }

        var received = new List<long>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await foreach (var item in channel.ReadAllAsync(cts.Token))
        {
            received.Add(item.Seq);
            if (received.Count == 64)
                break;
        }

        Assert.Equal(64, received.Count);
        Assert.Equal(137, received[0]);
        Assert.Equal(200, received[^1]);
    }

    [Fact]
    public async Task SensorTelemetryProcessor_PromotesConfirmedIntrusion_AndReroutesDisplacedVehicle()
    {
        var graph = StandardTopologyFactory.CreateStandardGarage();
        var allocationService = new ParkingAllocationService(
            graph,
            new AStarPathfinder(),
            new InMemoryBayLeaseManager(),
            new InMemoryEdgeCongestionTracker(),
            NullLogger<ParkingAllocationService>.Instance);

        var options = new SensorDebounceOptions { MinConsecutiveReadings = 2, StabilityWindowMs = 1500 };
        var channel = new TelemetryIngestionChannel(options);
        var processor = new SensorTelemetryProcessor(
            channel,
            new SensorReadingFilter(options),
            allocationService,
            NullLogger<SensorTelemetryProcessor>.Instance);

        var allocated = await allocationService.AllocateAsync(
            new AllocateSpotRequest("GATE-N1", "CAR-LIVE-1", VehicleType.Standard));
        Assert.True(allocated.Succeeded);

        var leasedBay = allocated.Session!.AssignedBayId;
        var t0 = DateTimeOffset.UtcNow;

        var firstOutcome = await processor.ProcessSingleAsync(
            new BaySensorReading(leasedBay, true, 45, 1, t0, "INTRUDER-X"));
        Assert.Equal(SensorFilterOutcome.Debouncing, firstOutcome);
        Assert.Equal(BayStatus.Leased, graph.BaysById[leasedBay].Status);

        var secondOutcome = await processor.ProcessSingleAsync(
            new BaySensorReading(leasedBay, true, 42, 2, t0.AddSeconds(2), "INTRUDER-X"));
        Assert.Equal(SensorFilterOutcome.ConfirmedTransition, secondOutcome);
        Assert.Equal(BayStatus.Occupied, graph.BaysById[leasedBay].Status);

        var active = Assert.Single(allocationService.GetActiveSessions());
        Assert.Equal("CAR-LIVE-1", active.VehicleSessionId);
        Assert.NotEqual(leasedBay, active.AssignedBayId);
    }
}
