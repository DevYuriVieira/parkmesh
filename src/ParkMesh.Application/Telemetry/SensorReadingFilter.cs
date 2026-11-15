using System.Collections.Concurrent;

namespace ParkMesh.Application.Telemetry;

public enum SensorFilterOutcome
{
    StaleSequence = 0,
    Unchanged = 1,
    Debouncing = 2,
    ConfirmedTransition = 3
}

public interface ISensorReadingFilter
{
    SensorFilterOutcome Evaluate(BaySensorReading reading);
}

public sealed class SensorReadingFilter : ISensorReadingFilter
{
    private sealed class BayState
    {
        public readonly Lock Sync = new();
        public long LastSeq = -1;
        public bool ConfirmedOccupied;
        public bool? PendingOccupied;
        public int ConsecutiveCount;
        public DateTimeOffset PendingSinceUtc;
    }

    private readonly ConcurrentDictionary<(string Tenant, string Bay), BayState> _states = new();
    private readonly int _minConsecutiveReadings;
    private readonly TimeSpan _stabilityWindow;

    public SensorReadingFilter(SensorDebounceOptions? options = null)
    {
        var opts = options ?? new SensorDebounceOptions();
        _minConsecutiveReadings = Math.Max(1, opts.MinConsecutiveReadings);
        _stabilityWindow = TimeSpan.FromMilliseconds(Math.Max(0, opts.StabilityWindowMs));
    }

    public SensorFilterOutcome Evaluate(BaySensorReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentException.ThrowIfNullOrWhiteSpace(reading.BayId);

        var key = (reading.TenantId ?? "default", reading.BayId.ToUpperInvariant());
        var state = _states.GetOrAdd(key, static _ => new BayState());

        lock (state.Sync)
        {
            if (reading.Seq <= state.LastSeq)
                return SensorFilterOutcome.StaleSequence;

            state.LastSeq = reading.Seq;

            if (reading.Occupied == state.ConfirmedOccupied)
            {
                state.PendingOccupied = null;
                state.ConsecutiveCount = 0;
                return SensorFilterOutcome.Unchanged;
            }

            if (state.PendingOccupied != reading.Occupied)
            {
                state.PendingOccupied = reading.Occupied;
                state.PendingSinceUtc = reading.TimestampUtc;
                state.ConsecutiveCount = 1;
            }
            else
            {
                state.ConsecutiveCount++;
            }

            var elapsed = reading.TimestampUtc - state.PendingSinceUtc;
            if (state.ConsecutiveCount >= _minConsecutiveReadings && elapsed >= _stabilityWindow)
            {
                state.ConfirmedOccupied = reading.Occupied;
                state.PendingOccupied = null;
                state.ConsecutiveCount = 0;
                return SensorFilterOutcome.ConfirmedTransition;
            }

            return SensorFilterOutcome.Debouncing;
        }
    }
}
