using System.Threading.Channels;

namespace ParkMesh.Application.Telemetry;

public sealed record TelemetryMetricsSnapshot(
    long EnqueuedCount,
    long ProcessedCount,
    long ConfirmedTransitionsCount,
    long DebouncedCount,
    long StaleDroppedCount);

public interface ITelemetryIngestionChannel
{
    bool TryEnqueue(BaySensorReading reading);
    IAsyncEnumerable<BaySensorReading> ReadAllAsync(CancellationToken ct = default);
    void RecordOutcome(SensorFilterOutcome outcome);
    TelemetryMetricsSnapshot GetMetrics();
}

public sealed class TelemetryIngestionChannel : ITelemetryIngestionChannel
{
    private readonly Channel<BaySensorReading> _channel;
    private long _enqueuedCount;
    private long _processedCount;
    private long _confirmedTransitionsCount;
    private long _debouncedCount;
    private long _staleDroppedCount;

    public TelemetryIngestionChannel(SensorDebounceOptions? options = null)
    {
        var capacity = Math.Max(64, (options ?? new SensorDebounceOptions()).ChannelCapacity);
        _channel = Channel.CreateBounded<BaySensorReading>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public bool TryEnqueue(BaySensorReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        if (_channel.Writer.TryWrite(reading))
        {
            Interlocked.Increment(ref _enqueuedCount);
            return true;
        }

        return false;
    }

    public IAsyncEnumerable<BaySensorReading> ReadAllAsync(CancellationToken ct = default) =>
        _channel.Reader.ReadAllAsync(ct);

    public void RecordOutcome(SensorFilterOutcome outcome)
    {
        Interlocked.Increment(ref _processedCount);
        switch (outcome)
        {
            case SensorFilterOutcome.ConfirmedTransition:
                Interlocked.Increment(ref _confirmedTransitionsCount);
                break;
            case SensorFilterOutcome.Debouncing:
                Interlocked.Increment(ref _debouncedCount);
                break;
            case SensorFilterOutcome.StaleSequence:
                Interlocked.Increment(ref _staleDroppedCount);
                break;
        }
    }

    public TelemetryMetricsSnapshot GetMetrics() =>
        new(
            Interlocked.Read(ref _enqueuedCount),
            Interlocked.Read(ref _processedCount),
            Interlocked.Read(ref _confirmedTransitionsCount),
            Interlocked.Read(ref _debouncedCount),
            Interlocked.Read(ref _staleDroppedCount));
}
