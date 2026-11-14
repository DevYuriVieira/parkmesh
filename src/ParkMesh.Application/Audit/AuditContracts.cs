using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using ParkMesh.Domain;

namespace ParkMesh.Application.Audit;

public enum AuditEventType
{
    BayAllocated = 0,
    LeaseReleased = 1,
    LeaseExpired = 2,
    PhysicalOccupied = 3,
    PhysicalVacated = 4,
    IntrusionRerouted = 5,
    BayOutOfService = 6,
    BayRestored = 7
}

public sealed record ParkingAuditEvent(
    DateTimeOffset OccurredAtUtc,
    AuditEventType EventType,
    string BayId,
    string? SessionId,
    long? FencingToken,
    VehicleType? VehicleType,
    string? NodeId,
    string? Details,
    long Id = 0);

public interface IParkingAuditSink
{
    bool TryEnqueue(ParkingAuditEvent auditEvent);
    ValueTask<bool> WaitToReadAsync(CancellationToken ct = default);
    bool TryRead([NotNullWhen(true)] out ParkingAuditEvent? auditEvent);
    long DroppedCount { get; }
}

public interface IParkingAuditStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task RecordBatchAsync(IReadOnlyList<ParkingAuditEvent> events, CancellationToken ct = default);
    Task<IReadOnlyList<ParkingAuditEvent>> GetRecentAsync(int limit = 50, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, AuditEventType>> GetLatestBayPhysicalStatesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, AuditEventType>>(
            new Dictionary<string, AuditEventType>(StringComparer.OrdinalIgnoreCase));
    Task<bool> CheckConnectivityAsync(CancellationToken ct = default);
}

public sealed class AuditChannelSink : IParkingAuditSink
{
    private readonly Channel<ParkingAuditEvent> _channel;
    private long _droppedCount;

    public AuditChannelSink(int capacity = 8192)
    {
        var boundedCapacity = Math.Max(128, capacity);
        _channel = Channel.CreateBounded<ParkingAuditEvent>(new BoundedChannelOptions(boundedCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    public bool TryEnqueue(ParkingAuditEvent auditEvent)
    {
        if (_channel.Writer.TryWrite(auditEvent))
            return true;

        Interlocked.Increment(ref _droppedCount);
        return false;
    }

    public ValueTask<bool> WaitToReadAsync(CancellationToken ct = default) =>
        _channel.Reader.WaitToReadAsync(ct);

    public bool TryRead([NotNullWhen(true)] out ParkingAuditEvent? auditEvent) =>
        _channel.Reader.TryRead(out auditEvent);
}

public sealed class NullParkingAuditStore : IParkingAuditStore
{
    public static readonly NullParkingAuditStore Instance = new();

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task RecordBatchAsync(IReadOnlyList<ParkingAuditEvent> events, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<ParkingAuditEvent>> GetRecentAsync(int limit = 50, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ParkingAuditEvent>>(Array.Empty<ParkingAuditEvent>());

    public Task<bool> CheckConnectivityAsync(CancellationToken ct = default) => Task.FromResult(true);
}

public sealed class NullParkingAuditSink : IParkingAuditSink
{
    public static readonly NullParkingAuditSink Instance = new();

    public long DroppedCount => 0;

    public bool TryEnqueue(ParkingAuditEvent auditEvent) => true;

    public ValueTask<bool> WaitToReadAsync(CancellationToken ct = default) => ValueTask.FromResult(false);

    public bool TryRead([NotNullWhen(true)] out ParkingAuditEvent? auditEvent)
    {
        auditEvent = null;
        return false;
    }
}
