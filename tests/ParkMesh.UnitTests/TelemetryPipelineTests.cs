using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ParkMesh.Application.Abstractions;
using ParkMesh.Application.Allocation;
using ParkMesh.Application.Routing;
using ParkMesh.Application.Telemetry;
using ParkMesh.Domain;
using ParkMesh.Infrastructure.Mqtt;
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

    [Fact]
    public void SensorReadingFilter_KeepsIndependentStatePerTenant()
    {
        var filter = new SensorReadingFilter(new SensorDebounceOptions { MinConsecutiveReadings = 2, StabilityWindowMs = 0 });
        var t0 = DateTimeOffset.UtcNow;

        Assert.Equal(SensorFilterOutcome.Debouncing, filter.Evaluate(new BaySensorReading("F0-B01", true, 30, 1, t0, null, "tenant-a")));
        // Same bay and same sequence number on another tenant must not be treated as stale.
        Assert.Equal(SensorFilterOutcome.Debouncing, filter.Evaluate(new BaySensorReading("F0-B01", true, 30, 1, t0, null, "tenant-b")));
        Assert.Equal(SensorFilterOutcome.ConfirmedTransition, filter.Evaluate(new BaySensorReading("F0-B01", true, 30, 2, t0.AddSeconds(1), null, "tenant-a")));
    }

    [Fact]
    public async Task SensorTelemetryProcessor_ReconcilesOnlyTheReadingTenant()
    {
        var tenantContext = new AsyncLocalTenantContext();
        var allocationService = new ParkingAllocationService(
            StandardTopologyFactory.CreateStandardGarage(),
            new AStarPathfinder(),
            new InMemoryBayLeaseManager(tenantContext),
            new InMemoryEdgeCongestionTracker(),
            NullLogger<ParkingAllocationService>.Instance,
            tenantContext: tenantContext);

        var options = new SensorDebounceOptions { MinConsecutiveReadings = 1, StabilityWindowMs = 0 };
        var processor = new SensorTelemetryProcessor(
            new TelemetryIngestionChannel(options),
            new SensorReadingFilter(options),
            allocationService,
            NullLogger<SensorTelemetryProcessor>.Instance,
            tenantContext);

        var outcome = await processor.ProcessSingleAsync(
            new BaySensorReading("F0-B05", true, 40, 1, DateTimeOffset.UtcNow, "CAR-A", "tenant-a"));
        Assert.Equal(SensorFilterOutcome.ConfirmedTransition, outcome);

        using (tenantContext.BeginScope("tenant-a"))
            Assert.Equal(BayStatus.Occupied, allocationService.Graph.BaysById["F0-B05"].Status);
        using (tenantContext.BeginScope("tenant-b"))
            Assert.Equal(BayStatus.Free, allocationService.Graph.BaysById["F0-B05"].Status);
        using (tenantContext.BeginScope("default"))
            Assert.Equal(BayStatus.Free, allocationService.Graph.BaysById["F0-B05"].Status);
    }

    [Fact]
    public async Task SensorReadingFilter_AndProcessor_NormalizeBlankTenantIdToDefault()
    {
        var tenantContext = new AsyncLocalTenantContext();
        var allocationService = new ParkingAllocationService(
            StandardTopologyFactory.CreateStandardGarage(),
            new AStarPathfinder(),
            new InMemoryBayLeaseManager(tenantContext),
            new InMemoryEdgeCongestionTracker(),
            NullLogger<ParkingAllocationService>.Instance,
            tenantContext: tenantContext);

        var options = new SensorDebounceOptions { MinConsecutiveReadings = 2, StabilityWindowMs = 0 };
        var filter = new SensorReadingFilter(options);
        var processor = new SensorTelemetryProcessor(
            new TelemetryIngestionChannel(options),
            filter,
            allocationService,
            NullLogger<SensorTelemetryProcessor>.Instance,
            tenantContext);

        var t0 = DateTimeOffset.UtcNow;
        Assert.Equal(SensorFilterOutcome.Debouncing, await processor.ProcessSingleAsync(new BaySensorReading("F0-B07", true, 40, 1, t0, "CAR-BLANK", "")));
        // Seq 1 on whitespace TenantId shares the "default" debounce bucket and must be rejected as stale.
        Assert.Equal(SensorFilterOutcome.StaleSequence, await processor.ProcessSingleAsync(new BaySensorReading("F0-B07", true, 40, 1, t0.AddSeconds(1), "CAR-BLANK", "   ")));
        // Seq 2 with explicit "default" completes the transition and reconciles the default tenant.
        Assert.Equal(SensorFilterOutcome.ConfirmedTransition, await processor.ProcessSingleAsync(new BaySensorReading("F0-B07", true, 40, 2, t0.AddSeconds(2), "CAR-BLANK", "default")));

        using (tenantContext.BeginScope("default"))
            Assert.Equal(BayStatus.Occupied, allocationService.Graph.BaysById["F0-B07"].Status);
    }

    [Fact]
    public async Task MqttConnectionManager_EnsureConnectedAsync_ReturnsFalseWhenBrokerUnreachable()
    {
        await using var manager = new MqttConnectionManager(
            Options.Create(new MqttOptions { Enabled = true, Host = "127.0.0.1", Port = 1 }),
            NullLogger<MqttConnectionManager>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connected = await manager.EnsureConnectedAsync(cts.Token);

        Assert.False(connected);
        Assert.False(manager.IsConnected);
    }
}
