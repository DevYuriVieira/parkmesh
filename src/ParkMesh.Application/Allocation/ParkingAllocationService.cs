using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ParkMesh.Application.Abstractions;
using ParkMesh.Application.Routing;
using ParkMesh.Application.Telemetry;
using ParkMesh.Domain;

namespace ParkMesh.Application.Allocation;

public interface IParkingAllocationService
{
    ParkingGraph Graph { get; }

    Task<AllocateSpotResult> AllocateAsync(AllocateSpotRequest request, CancellationToken ct = default);

    Task<bool> ReleaseLeaseAsync(string bayId, long fencingToken, CancellationToken ct = default);

    Task<PhysicalOccupancyResult> ReconcilePhysicalOccupancyAsync(
        string bayId,
        bool occupied,
        string? occupantSessionId = null,
        CancellationToken ct = default);

    bool UpdateVehicleProgress(string vehicleSessionId, string currentNodeId);

    ValueTask<bool> UpdateVehicleProgressAsync(string vehicleSessionId, string currentNodeId, CancellationToken ct = default);

    IReadOnlyCollection<ActiveVehicleSession> GetActiveSessions();
}

public sealed class ParkingAllocationService : IParkingAllocationService
{
    private static readonly TimeSpan DefaultLeaseTtl = TimeSpan.FromSeconds(90);

    private readonly ParkingGraph _graph;
    private readonly IPathfinder _pathfinder;
    private readonly IBayLeaseManager _leaseManager;
    private readonly IEdgeCongestionTracker _congestionTracker;
    private readonly ILedPanelPublisher _ledPublisher;
    private readonly IParkingEventSink _eventSink;
    private readonly IEngineMetricsRecorder _metrics;
    private readonly ILogger<ParkingAllocationService> _logger;
    private readonly ConcurrentDictionary<string, ActiveVehicleSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _stateLock = new();

    public ParkingAllocationService(
        ParkingGraph graph,
        IPathfinder pathfinder,
        IBayLeaseManager leaseManager,
        IEdgeCongestionTracker congestionTracker,
        ILogger<ParkingAllocationService> logger,
        ILedPanelPublisher? ledPublisher = null,
        IParkingEventSink? eventSink = null,
        IEngineMetricsRecorder? metrics = null)
    {
        _graph = graph;
        _pathfinder = pathfinder;
        _leaseManager = leaseManager;
        _congestionTracker = congestionTracker;
        _logger = logger;
        _ledPublisher = ledPublisher ?? NullLedPanelPublisher.Instance;
        _eventSink = eventSink ?? NullParkingEventSink.Instance;
        _metrics = metrics ?? NullEngineMetricsRecorder.Instance;
    }

    public ParkingGraph Graph => _graph;

    public async Task<AllocateSpotResult> AllocateAsync(AllocateSpotRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StartNodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.VehicleSessionId);

        if (!_graph.TryGetNode(request.StartNodeId, out _))
        {
            return new AllocateSpotResult(false, FailureReason: $"Unknown start node '{request.StartNodeId}'.");
        }

        using var activity = EngineMetricsRecorder.ActivitySource.StartActivity("parkmesh.allocate");
        activity?.SetTag("parkmesh.gate_id", request.StartNodeId);
        activity?.SetTag("parkmesh.vehicle_type", request.VehicleType.ToString());

        var sw = Stopwatch.StartNew();
        var ttl = request.LeaseTtl ?? DefaultLeaseTtl;
        var congestionSnapshot = _congestionTracker.Snapshot();

        var astarSw = Stopwatch.StartNew();
        IReadOnlyList<ParkingRoute> candidates;
        lock (_stateLock)
        {
            candidates = _pathfinder.FindCandidateBayRoutes(
                _graph,
                request.StartNodeId,
                request.VehicleType,
                congestionSnapshot,
                maxCandidates: 15);
        }
        astarSw.Stop();
        _metrics.RecordAStarLatency(astarSw.Elapsed.TotalMilliseconds);

        if (candidates.Count == 0)
        {
            sw.Stop();
            return new AllocateSpotResult(
                false,
                FailureReason: $"No compatible free bay available for vehicle type '{request.VehicleType}'.",
                ComputationTimeMs: sw.Elapsed.TotalMilliseconds);
        }

        foreach (var candidateRoute in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var leaseSw = Stopwatch.StartNew();
            var lease = await _leaseManager.TryAcquireLeaseAsync(
                candidateRoute.TargetBayId,
                request.VehicleSessionId,
                ttl,
                ct);
            leaseSw.Stop();
            _metrics.RecordRedisLeaseLatency(leaseSw.Elapsed.TotalMilliseconds);

            if (!lease.Acquired)
            {
                _metrics.RecordContentionRetry();
                _logger.LogDebug(
                    "Lease contention on bay {BayId} for session {SessionId}; falling back to next candidate.",
                    candidateRoute.TargetBayId,
                    request.VehicleSessionId);
                continue;
            }

            var expiresAt = lease.ExpiresAtUtc ?? DateTimeOffset.UtcNow.Add(ttl);
            bool committedLocally;
            lock (_stateLock)
            {
                var bay = _graph.BaysById[candidateRoute.TargetBayId];
                committedLocally = bay.TrySetLeased(request.VehicleSessionId, lease.FencingToken, expiresAt);
            }

            if (!committedLocally)
            {
                await _leaseManager.TryReleaseLeaseAsync(candidateRoute.TargetBayId, lease.FencingToken, ct);
                continue;
            }

            var session = new ActiveVehicleSession(
                request.VehicleSessionId,
                request.VehicleType,
                candidateRoute.TargetBayId,
                lease.FencingToken,
                candidateRoute,
                request.StartNodeId,
                DateTimeOffset.UtcNow,
                expiresAt);

            _sessions[request.VehicleSessionId] = session;
            _congestionTracker.AddRouteLoad(candidateRoute.Steps.Select(s => s.EdgeId));
            _metrics.RecordAllocation();
            activity?.SetTag("parkmesh.bay_id", candidateRoute.TargetBayId);
            activity?.SetTag("parkmesh.fencing_token", lease.FencingToken);

            await _ledPublisher.PublishDirectivesAsync(candidateRoute.LedDirectives, ct);
            await _eventSink.OnBayAllocatedAsync(session, ct);

            sw.Stop();
            _logger.LogInformation(
                "Allocated bay {BayId} (fence={FencingToken}, cost={Cost:F2}) to session {SessionId} in {ElapsedMs:F2}ms.",
                candidateRoute.TargetBayId,
                lease.FencingToken,
                candidateRoute.TotalCost,
                request.VehicleSessionId,
                sw.Elapsed.TotalMilliseconds);

            return new AllocateSpotResult(true, session, ComputationTimeMs: sw.Elapsed.TotalMilliseconds);
        }

        sw.Stop();
        return new AllocateSpotResult(
            false,
            FailureReason: "All candidate bays were claimed concurrently. Please retry.",
            ComputationTimeMs: sw.Elapsed.TotalMilliseconds);
    }

    public async Task<bool> ReleaseLeaseAsync(string bayId, long fencingToken, CancellationToken ct = default)
    {
        if (!_graph.TryGetBay(bayId, out var bay))
            return false;

        var releasedInRedis = await _leaseManager.TryReleaseLeaseAsync(bayId, fencingToken, ct);
        if (!releasedInRedis)
            return false;

        ActiveVehicleSession? removedSession = null;
        lock (_stateLock)
        {
            if (bay.CurrentSessionId is not null && bay.FencingToken == fencingToken)
            {
                _sessions.TryRemove(bay.CurrentSessionId, out removedSession);
            }

            bay.TryReleaseLease(fencingToken);
        }

        if (removedSession is not null)
        {
            _congestionTracker.RemoveRouteLoad(removedSession.Route.Steps.Select(s => s.EdgeId));
        }

        return true;
    }

    public async Task<PhysicalOccupancyResult> ReconcilePhysicalOccupancyAsync(
        string bayId,
        bool occupied,
        string? occupantSessionId = null,
        CancellationToken ct = default)
    {
        if (!_graph.TryGetBay(bayId, out var bay))
            throw new KeyNotFoundException($"Unknown bay '{bayId}'.");

        if (!occupied)
        {
            await _leaseManager.InvalidateLeaseAsync(bayId, ct);
            ActiveVehicleSession? departed = null;
            lock (_stateLock)
            {
                if (bay.CurrentSessionId is not null)
                    _sessions.TryRemove(bay.CurrentSessionId, out departed);

                bay.MarkFree();
            }

            if (departed is not null)
                _congestionTracker.RemoveRouteLoad(departed.Route.Steps.Select(s => s.EdgeId));

            await _eventSink.OnPhysicalOccupancyChangedAsync(bayId, BayStatus.Free, ct);
            return new PhysicalOccupancyResult(bayId, BayStatus.Free, false, null, null);
        }

        ActiveVehicleSession? displacedSession = null;
        var wasIntruder = false;

        lock (_stateLock)
        {
            if (bay.Status == BayStatus.Leased &&
                bay.CurrentSessionId is not null &&
                !string.Equals(bay.CurrentSessionId, occupantSessionId, StringComparison.OrdinalIgnoreCase))
            {
                wasIntruder = true;
                _sessions.TryRemove(bay.CurrentSessionId, out displacedSession);
            }
            else if (bay.CurrentSessionId is not null &&
                     string.Equals(bay.CurrentSessionId, occupantSessionId, StringComparison.OrdinalIgnoreCase))
            {
                _sessions.TryRemove(bay.CurrentSessionId, out var arrivedSession);
                if (arrivedSession is not null)
                    _congestionTracker.RemoveRouteLoad(arrivedSession.Route.Steps.Select(s => s.EdgeId));
            }

            bay.MarkOccupied(occupantSessionId);
        }

        await _leaseManager.InvalidateLeaseAsync(bayId, ct);

        if (!wasIntruder || displacedSession is null)
        {
            await _eventSink.OnPhysicalOccupancyChangedAsync(bayId, BayStatus.Occupied, ct);
            return new PhysicalOccupancyResult(bayId, BayStatus.Occupied, false, null, null);
        }

        _congestionTracker.RemoveRouteLoad(displacedSession.Route.Steps.Select(s => s.EdgeId));

        _logger.LogWarning(
            "Physical intrusion on leased bay {BayId}! Displaced session {SessionId}; triggering live reroute from {LastNode}.",
            bayId,
            displacedSession.VehicleSessionId,
            displacedSession.LastKnownNodeId);

        _metrics.RecordReroute();
        var rerouteResult = await AllocateAsync(
            new AllocateSpotRequest(
                displacedSession.LastKnownNodeId,
                displacedSession.VehicleSessionId,
                displacedSession.VehicleType),
            ct);

        var newSession = rerouteResult.Succeeded ? rerouteResult.Session : null;
        await _eventSink.OnPhysicalOccupancyChangedAsync(bayId, BayStatus.Occupied, ct);
        await _eventSink.OnRouteReassignedAsync(bayId, displacedSession.VehicleSessionId, newSession, ct);

        return new PhysicalOccupancyResult(
            bayId,
            BayStatus.Occupied,
            WasIntruder: true,
            DisplacedSessionId: displacedSession.VehicleSessionId,
            ReroutedSession: newSession);
    }

    public bool UpdateVehicleProgress(string vehicleSessionId, string currentNodeId)
    {
        if (!_graph.TryGetNode(currentNodeId, out _))
            return false;

        if (!_sessions.TryGetValue(vehicleSessionId, out var current))
            return false;

        _sessions[vehicleSessionId] = current with { LastKnownNodeId = currentNodeId };
        return true;
    }

    public async ValueTask<bool> UpdateVehicleProgressAsync(
        string vehicleSessionId,
        string currentNodeId,
        CancellationToken ct = default)
    {
        if (!UpdateVehicleProgress(vehicleSessionId, currentNodeId))
            return false;

        await _eventSink.OnVehicleMovedAsync(vehicleSessionId, currentNodeId, ct);
        return true;
    }

    public IReadOnlyCollection<ActiveVehicleSession> GetActiveSessions() => _sessions.Values.ToArray();
}
