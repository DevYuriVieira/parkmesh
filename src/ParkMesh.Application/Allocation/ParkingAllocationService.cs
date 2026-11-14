using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ParkMesh.Application.Abstractions;
using ParkMesh.Application.Audit;
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

    Task<IReadOnlyList<ExpiredLeaseInfo>> PurgeExpiredSessionsAsync(CancellationToken ct = default);

    Task<int> RehydrateStateAsync(CancellationToken ct = default);
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
    private readonly IParkingAuditSink _auditSink;
    private readonly IParkingAuditStore _auditStore;
    private readonly TimeProvider _timeProvider;
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
        IEngineMetricsRecorder? metrics = null,
        TimeProvider? timeProvider = null,
        IParkingAuditSink? auditSink = null,
        IParkingAuditStore? auditStore = null)
    {
        _graph = graph;
        _pathfinder = pathfinder;
        _leaseManager = leaseManager;
        _congestionTracker = congestionTracker;
        _logger = logger;
        _ledPublisher = ledPublisher ?? NullLedPanelPublisher.Instance;
        _eventSink = eventSink ?? NullParkingEventSink.Instance;
        _metrics = metrics ?? NullEngineMetricsRecorder.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _auditSink = auditSink ?? NullParkingAuditSink.Instance;
        _auditStore = auditStore ?? NullParkingAuditStore.Instance;
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

            var allocatedAt = _timeProvider.GetUtcNow();
            var expiresAt = lease.ExpiresAtUtc ?? allocatedAt.Add(ttl);
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
                allocatedAt,
                expiresAt);

            _sessions[request.VehicleSessionId] = session;
            _congestionTracker.AddRouteLoad(candidateRoute.Steps.Select(s => s.EdgeId));
            _metrics.RecordAllocation();
            activity?.SetTag("parkmesh.bay_id", candidateRoute.TargetBayId);
            activity?.SetTag("parkmesh.fencing_token", lease.FencingToken);

            await _ledPublisher.PublishDirectivesAsync(candidateRoute.LedDirectives, ct);
            await _eventSink.OnBayAllocatedAsync(session, ct);
            _auditSink.TryEnqueue(new ParkingAuditEvent(
                allocatedAt,
                AuditEventType.BayAllocated,
                session.AssignedBayId,
                session.VehicleSessionId,
                session.FencingToken,
                session.VehicleType,
                request.StartNodeId,
                $"cost={candidateRoute.TotalCost:F2}"));

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

        _auditSink.TryEnqueue(new ParkingAuditEvent(
            _timeProvider.GetUtcNow(),
            AuditEventType.LeaseReleased,
            bayId,
            removedSession?.VehicleSessionId,
            fencingToken,
            removedSession?.VehicleType,
            removedSession?.LastKnownNodeId,
            null));

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
            _auditSink.TryEnqueue(new ParkingAuditEvent(
                _timeProvider.GetUtcNow(),
                AuditEventType.PhysicalVacated,
                bayId,
                departed?.VehicleSessionId,
                departed?.FencingToken,
                departed?.VehicleType,
                departed?.LastKnownNodeId,
                null));

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
            _auditSink.TryEnqueue(new ParkingAuditEvent(
                _timeProvider.GetUtcNow(),
                AuditEventType.PhysicalOccupied,
                bayId,
                occupantSessionId,
                null,
                null,
                null,
                null));
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
        _auditSink.TryEnqueue(new ParkingAuditEvent(
            _timeProvider.GetUtcNow(),
            AuditEventType.IntrusionRerouted,
            bayId,
            displacedSession.VehicleSessionId,
            newSession?.FencingToken,
            displacedSession.VehicleType,
            displacedSession.LastKnownNodeId,
            newSession is not null ? $"reroutedBay={newSession.AssignedBayId};intruder={occupantSessionId}" : $"rerouteFailed;intruder={occupantSessionId}"));

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

    public async Task<IReadOnlyList<ExpiredLeaseInfo>> PurgeExpiredSessionsAsync(CancellationToken ct = default)
    {
        var now = _timeProvider.GetUtcNow();
        var candidates = _sessions.Values.Where(s => s.ExpiresAtUtc <= now).ToArray();
        if (candidates.Length == 0)
            return Array.Empty<ExpiredLeaseInfo>();

        var expired = new List<ExpiredLeaseInfo>();
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            ActiveVehicleSession? purged = null;
            lock (_stateLock)
            {
                if (_sessions.TryGetValue(candidate.VehicleSessionId, out var current)
                    && current.FencingToken == candidate.FencingToken
                    && current.ExpiresAtUtc <= now
                    && _graph.TryGetBay(current.AssignedBayId, out var bay)
                    && bay.TryReleaseLease(current.FencingToken))
                {
                    _sessions.TryRemove(current.VehicleSessionId, out purged);
                }
            }

            if (purged is null)
                continue;

            _congestionTracker.RemoveRouteLoad(purged.Route.Steps.Select(s => s.EdgeId));
            await _leaseManager.TryReleaseLeaseAsync(purged.AssignedBayId, purged.FencingToken, ct);
            _metrics.RecordLeaseExpired();
            await _eventSink.OnPhysicalOccupancyChangedAsync(purged.AssignedBayId, BayStatus.Free, ct);
            _auditSink.TryEnqueue(new ParkingAuditEvent(
                now,
                AuditEventType.LeaseExpired,
                purged.AssignedBayId,
                purged.VehicleSessionId,
                purged.FencingToken,
                purged.VehicleType,
                purged.LastKnownNodeId,
                null));

            _logger.LogInformation(
                "Purged expired lease for bay {BayId} (fence={FencingToken}, session={SessionId}).",
                purged.AssignedBayId,
                purged.FencingToken,
                purged.VehicleSessionId);

            expired.Add(new ExpiredLeaseInfo(
                purged.AssignedBayId,
                purged.VehicleSessionId,
                purged.FencingToken,
                now));
        }

        return expired;
    }

    public async Task<int> RehydrateStateAsync(CancellationToken ct = default)
    {
        var physicalStates = await _auditStore.GetLatestBayPhysicalStatesAsync(ct);
        var bayIds = _graph.BaysById.Keys.ToArray();
        var activeLeases = await _leaseManager.GetActiveLeasesAsync(bayIds, ct);

        Dictionary<(string BayId, long Token), ParkingAuditEvent>? allocationAuditByLease = null;
        if (activeLeases.Count > 0)
        {
            var recentEvents = await _auditStore.GetRecentAsync(200, ct);
            allocationAuditByLease = new Dictionary<(string BayId, long Token), ParkingAuditEvent>();
            foreach (var ev in recentEvents)
            {
                if (ev.EventType == AuditEventType.BayAllocated && ev.FencingToken.HasValue)
                {
                    allocationAuditByLease.TryAdd((ev.BayId, ev.FencingToken.Value), ev);
                }
            }
        }

        var defaultGateId = _graph.Gates.FirstOrDefault()?.Id;
        var precomputedRoutes = new List<(ActiveLeaseSnapshot Lease, ParkingRoute Route, VehicleType VehicleType, string StartNodeId)>(activeLeases.Count);

        foreach (var lease in activeLeases)
        {
            if (!_graph.TryGetBay(lease.BayId, out var bay))
                continue;

            ParkingAuditEvent? allocEvent = null;
            allocationAuditByLease?.TryGetValue((lease.BayId, lease.FencingToken), out allocEvent);

            var startNodeId = allocEvent?.NodeId is not null && _graph.TryGetNode(allocEvent.NodeId, out _)
                ? allocEvent.NodeId
                : defaultGateId ?? bay.NodeId;

            var vehicleType = allocEvent?.VehicleType ?? bay.PrimaryType;
            var route = _pathfinder.FindShortestPath(_graph, startNodeId, bay.BayId)
                ?? _pathfinder.FindShortestPath(_graph, bay.NodeId, bay.BayId);

            if (route is null)
                continue;

            precomputedRoutes.Add((lease, route, vehicleType, startNodeId));
        }

        var now = _timeProvider.GetUtcNow();
        var restoredSessions = new List<ActiveVehicleSession>(precomputedRoutes.Count);
        var restoredCount = 0;

        lock (_stateLock)
        {
            foreach (var (bayId, eventType) in physicalStates)
            {
                if (!_graph.TryGetBay(bayId, out var bay))
                    continue;

                switch (eventType)
                {
                    case AuditEventType.PhysicalOccupied or AuditEventType.IntrusionRerouted:
                        if (bay.Status != BayStatus.Occupied)
                        {
                            bay.MarkOccupied();
                            restoredCount++;
                        }
                        break;

                    case AuditEventType.BayOutOfService:
                        if (bay.Status != BayStatus.OutOfService)
                        {
                            bay.MarkOutOfService();
                            restoredCount++;
                        }
                        break;

                    case AuditEventType.PhysicalVacated or AuditEventType.BayRestored:
                        if (bay.Status is BayStatus.Occupied or BayStatus.OutOfService)
                        {
                            bay.MarkFree();
                        }
                        break;
                }
            }

            foreach (var (lease, route, vehicleType, startNodeId) in precomputedRoutes)
            {
                if (!_graph.TryGetBay(lease.BayId, out var bay))
                    continue;

                var expiresAt = now.Add(lease.RemainingTtl is { } ttl && ttl > TimeSpan.Zero ? ttl : DefaultLeaseTtl);
                if (!bay.TrySetLeased(lease.VehicleSessionId, lease.FencingToken, expiresAt))
                    continue;

                var session = new ActiveVehicleSession(
                    lease.VehicleSessionId,
                    vehicleType,
                    lease.BayId,
                    lease.FencingToken,
                    route,
                    startNodeId,
                    now,
                    expiresAt);

                _sessions[lease.VehicleSessionId] = session;
                restoredSessions.Add(session);
                restoredCount++;
            }
        }

        foreach (var session in restoredSessions)
        {
            _congestionTracker.AddRouteLoad(session.Route.Steps.Select(s => s.EdgeId));
        }

        _logger.LogInformation(
            "Rehydrated garage state on startup: {RestoredCount} bay states ({LeaseCount} active leases).",
            restoredCount,
            restoredSessions.Count);

        return restoredCount;
    }
}
