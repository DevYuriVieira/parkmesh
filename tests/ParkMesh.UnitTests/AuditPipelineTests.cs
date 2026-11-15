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
        var store = new GateableAuditStore();
        var service = new ParkingAuditHostedService(sink, store, NullLogger<ParkingAuditHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await store.Initialized.Task;

        // Enqueue one trigger event to enter the first RecordBatchAsync and pause there,
        // then enqueue 150 more events before triggering shutdown so all remaining batches
        // must be drained inside the ExecuteAsync finally block.
        var now = DateTimeOffset.UtcNow;
        Assert.True(sink.TryEnqueue(new ParkingAuditEvent(
            now,
            AuditEventType.BayAllocated,
            "F0-B01",
            "DRAIN-TRIGGER",
            1,
            VehicleType.Standard,
            "GATE-N1",
            null)));

        await store.FirstBatchStarted.Task;

        const int remainingEvents = 150;
        for (var i = 0; i < remainingEvents; i++)
        {
            Assert.True(sink.TryEnqueue(new ParkingAuditEvent(
                now.AddMilliseconds(i + 1),
                AuditEventType.BayAllocated,
                $"F0-B{(i % 20) + 1:D2}",
                $"DRAIN-{i}",
                i + 2,
                VehicleType.Standard,
                "GATE-N1",
                null)));
        }

        var stopTask = service.StopAsync(CancellationToken.None);
        store.ReleaseFirstBatch.TrySetResult();
        await stopTask;

        var persisted = await store.Inner.GetRecentAsync(200);
        Assert.Equal(remainingEvents + 1, persisted.Count);
        Assert.False(sink.TryRead(out _));
    }

    private sealed class GateableAuditStore : IParkingAuditStore
    {
        public InMemoryParkingAuditStore Inner { get; } = new();
        public TaskCompletionSource Initialized { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstBatchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstBatch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _batchCount;

        public Task InitializeAsync(CancellationToken ct = default)
        {
            Initialized.TrySetResult();
            return Inner.InitializeAsync(ct);
        }

        public async Task RecordBatchAsync(IReadOnlyList<ParkingAuditEvent> events, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _batchCount) == 1)
            {
                FirstBatchStarted.TrySetResult();
                await ReleaseFirstBatch.Task;
            }

            await Inner.RecordBatchAsync(events, ct);
        }

        public Task<IReadOnlyList<ParkingAuditEvent>> GetRecentAsync(int limit = 50, CancellationToken ct = default) =>
            Inner.GetRecentAsync(limit, ct);

        public Task<bool> CheckConnectivityAsync(CancellationToken ct = default) =>
            Inner.CheckConnectivityAsync(ct);
    }

    [Fact]
    public async Task ParkingAuditHostedService_SurvivesTransientStoreFailureAndPersistsSubsequentBatch()
    {
        var sink = new AuditChannelSink(128);
        var store = new TransientFailingAuditStore();
        var service = new ParkingAuditHostedService(sink, store, NullLogger<ParkingAuditHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await store.Initialized.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var t0 = DateTimeOffset.UtcNow;
        Assert.True(sink.TryEnqueue(new ParkingAuditEvent(
            t0,
            AuditEventType.BayAllocated,
            "F0-B01",
            "DURING-OUTAGE",
            1,
            VehicleType.Standard,
            "GATE-N1",
            null)));

        await store.FirstBatchFailed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(sink.TryEnqueue(new ParkingAuditEvent(
            t0.AddSeconds(1),
            AuditEventType.BayAllocated,
            "F0-B02",
            "AFTER-OUTAGE",
            2,
            VehicleType.Standard,
            "GATE-N1",
            null)));

        await store.SecondBatchPersisted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        var persisted = await store.Inner.GetRecentAsync(10);
        Assert.Contains(persisted, e => e.SessionId == "AFTER-OUTAGE");
        Assert.DoesNotContain(persisted, e => e.SessionId == "DURING-OUTAGE");
    }

    private sealed class TransientFailingAuditStore : IParkingAuditStore
    {
        public InMemoryParkingAuditStore Inner { get; } = new();
        public TaskCompletionSource Initialized { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstBatchFailed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondBatchPersisted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _batchCount;

        public Task InitializeAsync(CancellationToken ct = default)
        {
            Initialized.TrySetResult();
            return Inner.InitializeAsync(ct);
        }

        public async Task RecordBatchAsync(IReadOnlyList<ParkingAuditEvent> events, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _batchCount) == 1)
            {
                FirstBatchFailed.TrySetResult();
                throw new InvalidOperationException("Simulated Postgres outage");
            }

            await Inner.RecordBatchAsync(events, ct);
            SecondBatchPersisted.TrySetResult();
        }

        public Task<IReadOnlyList<ParkingAuditEvent>> GetRecentAsync(int limit = 50, CancellationToken ct = default) =>
            Inner.GetRecentAsync(limit, ct);

        public Task<bool> CheckConnectivityAsync(CancellationToken ct = default) =>
            Inner.CheckConnectivityAsync(ct);
    }
}
