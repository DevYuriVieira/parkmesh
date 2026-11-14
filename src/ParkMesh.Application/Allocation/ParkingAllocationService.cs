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

    Task<PhysicalOccupancyResult> SetBayMaintenanceAsync(
        string bayId,
        bool outOfService,
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
    private readonly ITenantContext _tenantContext;
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
        IParkingAuditStore? auditStore = null,
        ITenantContext? tenantContext = null)
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
        _tenantContext = tenantContext ?? DefaultTenantContext.Instance;
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

        const int initialCandidateBatchSize = 15;
        var sw = Stopwatch.StartNew();
        var ttl = request.LeaseTtl ?? DefaultLeaseTtl;
        HashSet<string>? exhaustedBays = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var congestionSnapshot = _congestionTracker.Snapshot();
            var requestedCandidates = exhaustedBays is null
                ? initialCandidateBatchSize
                : Math.Min(_graph.BaysById.Count, exhaustedBays.Count + initialCandidateBatchSize);

            var astarSw = Stopwatch.StartNew();
            IReadOnlyList<ParkingRoute> candidates;
            lock (_stateLock)
            {
                candidates = _pathfinder.FindCandidateBayRoutes(
                    _graph,
                    request.StartNodeId,
                    request.VehicleType,
                    congestionSnapshot,
                    maxCandidates: requestedCandidates);
            }
            astarSw.Stop();
            _metrics.RecordAStarLatency(astarSw.Elapsed.TotalMilliseconds);

            if (candidates.Count == 0 && exhaustedBays is null)
            {
                sw.Stop();
                return new AllocateSpotResult(
                    false,
                    FailureReason: $"No compatible free bay available for vehicle type '{request.VehicleType}'.",
                    ComputationTimeMs: sw.Elapsed.TotalMilliseconds);
            }

            var attemptedNewCandidate = false;
            foreach (var candidateRoute in candidates)
            {
                ct.ThrowIfCancellationRequested();

                if (exhaustedBays is not null && exhaustedBays.Contains(candidateRoute.TargetBayId))
                    continue;

                attemptedNewCandidate = true;

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
                    exhaustedBays ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    exhaustedBays.Add(candidateRoute.TargetBayId);
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
                    exhaustedBays ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    exhaustedBays.Add(candidateRoute.TargetBayId);
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
                EnqueueAudit(
                    allocatedAt,
                    AuditEventType.BayAllocated,
                    session.AssignedBayId,
                    session.VehicleSessionId,
                    session.FencingToken,
                    session.VehicleType,
                    request.StartNodeId,
                    $"cost={candidateRoute.TotalCost:F2}");

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

            if (!attemptedNewCandidate || (exhaustedBays?.Count ?? 0) >= _graph.BaysById.Count)
                break;
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

        EnqueueAudit(
            _timeProvider.GetUtcNow(),
            AuditEventType.LeaseReleased,
            bayId,
            removedSession?.VehicleSessionId,
            fencingToken,
            removedSession?.VehicleType,
            removedSession?.LastKnownNodeId,
            null);

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
            EnqueueAudit(
                _timeProvider.GetUtcNow(),
                AuditEventType.PhysicalVacated,
                bayId,
                departed?.VehicleSessionId,
                departed?.FencingToken,
                departed?.VehicleType,
                departed?.LastKnownNodeId,
                null);

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
            EnqueueAudit(
                _timeProvider.GetUtcNow(),
                AuditEventType.PhysicalOccupied,
                bayId,
                occupantSessionId,
                null,
                null,
                null,
                null);
            return new PhysicalOccupancyResult(bayId, BayStatus.Occupied, false, null, null);
        }

        return await RerouteDisplacedSessionAsync(
            bayId,
            BayStatus.Occupied,
            displacedSession,
            wasIntruder: true,
            AuditEventType.IntrusionRerouted,
            rerouteSuccessDetails: newSession => $"reroutedBay={newSession.AssignedBayId};intruder={occupantSessionId}",
            rerouteFailureDetails: $"rerouteFailed;intruder={occupantSessionId}",
            warningReason: "Physical intrusion on leased bay",
            ct);
    }

    public async Task<PhysicalOccupancyResult> SetBayMaintenanceAsync(
        string bayId,
        bool outOfService,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bayId);

        if (!_graph.TryGetBay(bayId, out var bay))
            throw new KeyNotFoundException($"Unknown bay '{bayId}'.");

        if (!outOfService)
        {
            var restored = false;
            BayStatus resultingStatus;
            lock (_stateLock)
            {
                if (bay.Status == BayStatus.OutOfService)
                {
                    bay.MarkFree();
                    restored = true;
                }

                resultingStatus = bay.Status;
            }

            if (restored)
            {
                await _eventSink.OnPhysicalOccupancyChangedAsync(bayId, BayStatus.Free, ct);
                EnqueueAudit(
                    _timeProvider.GetUtcNow(),
                    AuditEventType.BayRestored,
                    bayId,
                    null,
                    null,
                    null,
                    null,
                    "maintenance cleared");
            }

            return new PhysicalOccupancyResult(bayId, resultingStatus, false, null, null);
        }

        ActiveVehicleSession? displacedSession = null;
        var changed = false;

        lock (_stateLock)
        {
            if (bay.Status == BayStatus.OutOfService)
                return new PhysicalOccupancyResult(bayId, BayStatus.OutOfService, false, null, null);

            if (bay.CurrentSessionId is not null)
                _sessions.TryRemove(bay.CurrentSessionId, out displacedSession);

            bay.MarkOutOfService();
            changed = true;
        }

        await _leaseManager.InvalidateLeaseAsync(bayId, ct);

        if (displacedSession is not null)
        {
            return await RerouteDisplacedSessionAsync(
                bayId,
                BayStatus.OutOfService,
                displacedSession,
                wasIntruder: false,
                AuditEventType.BayOutOfService,
                rerouteSuccessDetails: newSession => $"maintenance;reroutedBay={newSession.AssignedBayId}",
                rerouteFailureDetails: "maintenance;rerouteFailed",
                warningReason: "Maintenance OutOfService on leased bay",
                ct);
        }

        if (changed)
        {
            await _eventSink.OnPhysicalOccupancyChangedAsync(bayId, BayStatus.OutOfService, ct);
            EnqueueAudit(
                _timeProvider.GetUtcNow(),
                AuditEventType.BayOutOfService,
                bayId,
                null,
                null,
                null,
                null,
                "manual maintenance");
        }

        return new PhysicalOccupancyResult(bayId, BayStatus.OutOfService, false, null, null);
    }

    private async Task<PhysicalOccupancyResult> RerouteDisplacedSessionAsync(
        string bayId,
        BayStatus resultingBayStatus,
        ActiveVehicleSession displacedSession,
        bool wasIntruder,
        AuditEventType auditEventType,
        Func<ActiveVehicleSession, string> rerouteSuccessDetails,
        string rerouteFailureDetails,
        string warningReason,
        CancellationToken ct)
    {
        _congestionTracker.RemoveRouteLoad(displacedSession.Route.Steps.Select(s => s.EdgeId));

        _logger.LogWarning(
            "{Reason} {BayId}! Displaced session {SessionId}; triggering live reroute from {LastNode}.",
            warningReason,
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
        await _eventSink.OnPhysicalOccupancyChangedAsync(bayId, resultingBayStatus, ct);
        await _eventSink.OnRouteReassignedAsync(bayId, displacedSession.VehicleSessionId, newSession, ct);
        EnqueueAudit(
            _timeProvider.GetUtcNow(),
            auditEventType,
            bayId,
            displacedSession.VehicleSessionId,
            newSession?.FencingToken,
            displacedSession.VehicleType,
            displacedSession.LastKnownNodeId,
            newSession is not null ? rerouteSuccessDetails(newSession) : rerouteFailureDetails);

        return new PhysicalOccupancyResult(
            bayId,
            resultingBayStatus,
            WasIntruder: wasIntruder,
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
            EnqueueAudit(
                now,
                AuditEventType.LeaseExpired,
                purged.AssignedBayId,
                purged.VehicleSessionId,
                purged.FencingToken,
                purged.VehicleType,
                purged.LastKnownNodeId,
                null);

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
        var tenantId = _tenantContext.CurrentTenantId;
        var physicalStates = await _auditStore.GetLatestBayPhysicalStatesAsync(tenantId, ct);
        var bayIds = _graph.BaysById.Keys.ToArray();
        var activeLeases = await _leaseManager.GetActiveLeasesAsync(bayIds, ct);

        Dictionary<(string BayId, long Token), ParkingAuditEvent>? allocationAuditByLease = null;
        if (activeLeases.Count > 0)
        {
            var recentEvents = await _auditStore.GetRecentAsync(tenantId, 200, ct);
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

    private void EnqueueAudit(
        DateTimeOffset occurredAtUtc,
        AuditEventType eventType,
        string bayId,
        string? sessionId,
        long? fencingToken,
        VehicleType? vehicleType,
        string? nodeId,
        string? details)
    {
        _auditSink.TryEnqueue(new ParkingAuditEvent(
            occurredAtUtc,
            eventType,
            bayId,
            sessionId,
            fencingToken,
            vehicleType,
            nodeId,
            details,
            TenantId: _tenantContext.CurrentTenantId));
    }
}
