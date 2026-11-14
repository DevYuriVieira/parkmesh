using Microsoft.Extensions.Logging.Abstractions;
using ParkMesh.Application.Audit;
using ParkMesh.Domain;
using ParkMesh.Infrastructure.Audit;

namespace ParkMesh.UnitTests;

public sealed class AuditPipelineTests
{
    [Fact]
    public async Task InMemoryAuditStore_RecordsBatchesInReverseChronologicalOrder()
    {
        var store = new InMemoryParkingAuditStore();
        await store.InitializeAsync();
        Assert.True(await store.CheckConnectivityAsync());

        var now = DateTimeOffset.UtcNow;
        await store.RecordBatchAsync(
        [
            new ParkingAuditEvent(now, AuditEventType.BayAllocated, "F0-B01", "S-1", 1, VehicleType.Standard, "GATE-N1", "first"),
            new ParkingAuditEvent(now.AddSeconds(1), AuditEventType.LeaseReleased, "F0-B01", "S-1", 1, VehicleType.Standard, "GATE-N1", "second")
        ]);

        var recent = await store.GetRecentAsync(10);

        Assert.Equal(2, recent.Count);
        Assert.Equal(AuditEventType.LeaseReleased, recent[0].EventType);
        Assert.Equal(AuditEventType.BayAllocated, recent[1].EventType);
        Assert.True(recent[0].Id > recent[1].Id);
    }

    [Fact]
    public async Task AuditChannelSink_EnqueuesAndDrainsEventsAsync()
    {
        var sink = new AuditChannelSink(128);
        var ev = new ParkingAuditEvent(
            DateTimeOffset.UtcNow,
            AuditEventType.IntrusionRerouted,
            "F1-B10",
            "S-2",
            4,
            VehicleType.EV,
            "F1-I-W-N",
            "rerouted");

        Assert.True(sink.TryEnqueue(ev));
        Assert.True(await sink.WaitToReadAsync());
        Assert.True(sink.TryRead(out var drained));
        Assert.Equal(ev, drained);
    }

    [Fact]
    public async Task ParkingAuditHostedService_DrainsAllBufferedEventsAcrossMultipleBatchesOnStop()
    {
        var sink = new AuditChannelSink(256);
        var store = new InMemoryParkingAuditStore();
        var service = new ParkingAuditHostedService(sink, store, NullLogger<ParkingAuditHostedService>.Instance);

        var now = DateTimeOffset.UtcNow;
        const int totalEvents = 150;
        for (var i = 0; i < totalEvents; i++)
        {
            Assert.True(sink.TryEnqueue(new ParkingAuditEvent(
                now.AddMilliseconds(i),
                AuditEventType.BayAllocated,
                $"F0-B{(i % 20) + 1:D2}",
                $"DRAIN-{i}",
                i + 1,
                VehicleType.Standard,
                "GATE-N1",
                null)));
        }

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        var persisted = await store.GetRecentAsync(200);
        Assert.Equal(totalEvents, persisted.Count);
        Assert.False(sink.TryRead(out _));
    }
}
