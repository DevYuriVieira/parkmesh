using ParkMesh.Application.Audit;

namespace ParkMesh.Infrastructure.Audit;

public sealed class InMemoryParkingAuditStore : IParkingAuditStore
{
    private const int MaxEntries = 4096;

    private readonly LinkedList<ParkingAuditEvent> _events = new();
    private readonly Lock _lock = new();
    private long _nextId;

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task RecordBatchAsync(IReadOnlyList<ParkingAuditEvent> events, CancellationToken ct = default)
    {
        if (events.Count == 0)
            return Task.CompletedTask;

        lock (_lock)
        {
            foreach (var ev in events)
            {
                var persisted = ev with { Id = ++_nextId };
                _events.AddFirst(persisted);
                if (_events.Count > MaxEntries)
                    _events.RemoveLast();
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ParkingAuditEvent>> GetRecentAsync(int limit = 50, CancellationToken ct = default)
    {
        var take = Math.Clamp(limit, 1, 200);
        lock (_lock)
        {
            var result = _events.Take(take).ToArray();
            return Task.FromResult<IReadOnlyList<ParkingAuditEvent>>(result);
        }
    }

    public Task<IReadOnlyDictionary<string, AuditEventType>> GetLatestBayPhysicalStatesAsync(CancellationToken ct = default)
    {
        var states = new Dictionary<string, AuditEventType>(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            foreach (var ev in _events)
            {
                if (states.ContainsKey(ev.BayId))
                    continue;

                if (ev.EventType is AuditEventType.PhysicalOccupied
                    or AuditEventType.IntrusionRerouted
                    or AuditEventType.PhysicalVacated
                    or AuditEventType.BayOutOfService
                    or AuditEventType.BayRestored)
                {
                    states[ev.BayId] = ev.EventType;
                }
            }
        }

        return Task.FromResult<IReadOnlyDictionary<string, AuditEventType>>(states);
    }

    public Task<bool> CheckConnectivityAsync(CancellationToken ct = default) => Task.FromResult(true);
}
