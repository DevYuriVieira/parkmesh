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

    IReadOnlyCollection<ActiveVehicleSession> GetActiveSessions(string tenantId) =>
        GetActiveSessions().Where(s => string.Equals(s.TenantId, tenantId, StringComparison.Ordinal)).ToArray();

    Task<IReadOnlyList<ExpiredLeaseInfo>> PurgeExpiredSessionsAsync(CancellationToken ct = default);

    Task<int> RehydrateStateAsync(CancellationToken ct = default);

    Task<int> RehydrateStateAsync(IEnumerable<string> tenantIds, CancellationToken ct = default) =>
        RehydrateStateAsync(ct);

    IReadOnlyCollection<string> GetKnownTenants() => [TenantDefaults.DefaultTenantId];
}

public sealed class ParkingAllocationService : IParkingAllocationService
{
    private static readonly TimeSpan DefaultLeaseTtl = TimeSpan.FromSeconds(90);

    private readonly ParkingGraph _graphTemplate;
    private readonly IPathfinder _pathfinder;
    private readonly IBayLeaseManager _leaseManager;
    private readonly ILedPanelPublisher _ledPublisher;
    private readonly IParkingEventSink _eventSink;
    private readonly IEngineMetricsRecorder _metrics;
    private readonly IParkingAuditSink _auditSink;
    private readonly IParkingAuditStore _auditStore;
    private readonly ITenantContext _tenantContext;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ParkingAllocationService> _logger;
    private readonly ConcurrentDictionary<SessionKey, ActiveVehicleSession> _sessions = new(SessionKeyComparer.Instance);
    private readonly ConcurrentDictionary<string, TenantRuntime> _tenants = new(StringComparer.Ordinal);

    private sealed class TenantRuntime(ParkingGraph graph, IEdgeCongestionTracker congestion)
    {
        public ParkingGraph Graph { get; } = graph;
        public IEdgeCongestionTracker Congestion { get; } = congestion;
        public Lock StateLock { get; } = new();
    }

    private readonly record struct SessionKey(string TenantId, string VehicleSessionId);

    private sealed class SessionKeyComparer : IEqualityComparer<SessionKey>
    {
        public static readonly SessionKeyComparer Instance = new();

        public bool Equals(SessionKey x, SessionKey y) =>
            string.Equals(x.TenantId, y.TenantId, StringComparison.Ordinal) &&
            string.Equals(x.VehicleSessionId, y.VehicleSessionId, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(SessionKey obj)
        {
            var hc = new HashCode();
            hc.Add(obj.TenantId, StringComparer.Ordinal);
            hc.Add(obj.VehicleSessionId, StringComparer.OrdinalIgnoreCase);
            return hc.ToHashCode();
        }
    }

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
        _graphTemplate = graph;
        _pathfinder = pathfinder;
        _leaseManager = leaseManager;
        _logger = logger;
        _ledPublisher = ledPublisher ?? NullLedPanelPublisher.Instance;
        _eventSink = eventSink ?? NullParkingEventSink.Instance;
        _metrics = metrics ?? NullEngineMetricsRecorder.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _auditSink = auditSink ?? NullParkingAuditSink.Instance;
        _auditStore = auditStore ?? NullParkingAuditStore.Instance;
        _tenantContext = tenantContext ?? DefaultTenantContext.Instance;
        _tenants[TenantDefaults.DefaultTenantId] = new TenantRuntime(graph, congestionTracker);
    }

    public ParkingGraph Graph => GetRuntime(_tenantContext.CurrentTenantId).Graph;

    public IReadOnlyCollection<string> GetKnownTenants() => _tenants.Keys.ToArray();

    private TenantRuntime GetRuntime(string tenantId) =>
        _tenants.GetOrAdd(
            tenantId,
            _ => new TenantRuntime(_graphTemplate.CloneTopology(), new InMemoryEdgeCongestionTracker()));

    public async Task<AllocateSpotResult> AllocateAsync(AllocateSpotRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StartNodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.VehicleSessionId);

        var currentTenant = _tenantContext.CurrentTenantId;
        var runtime = GetRuntime(currentTenant);

        if (!runtime.Graph.TryGetNode(request.StartNodeId, out _))
        {
            return new AllocateSpotResult(false, FailureReason: $"Unknown start node '{request.StartNodeId}'.");
        }

        using var activity = EngineMetricsRecorder.ActivitySource.StartActivity("parkmesh.allocate");
        activity?.SetTag("parkmesh.gate_id", request.StartNodeId);
        activity?.SetTag("parkmesh.vehicle_type", request.VehicleType.ToString());
        activity?.SetTag("parkmesh.tenant_id", currentTenant);

        const int initialCandidateBatchSize = 15;
        var sw = Stopwatch.StartNew();
        var sessionKey = new SessionKey(currentTenant, request.VehicleSessionId);

        // Idempotency & conflict guard: if the caller retries the exact same session
        // (same tenant, gate, and vehicle type) while its lease is still active, return
        // the existing allocation instead of grabbing a second bay. If parameters differ,
        // reject with a conflict so one session cannot hold two bays.
        if (_sessions.TryGetValue(sessionKey, out var existing) &&
            runtime.Graph.TryGetBay(existing.AssignedBayId, out var existingBay))
        {
            var now = _timeProvider.GetUtcNow();
            bool stillActive;
            lock (runtime.StateLock)
            {
                stillActive = existingBay.Status == BayStatus.Leased
                    && string.Equals(existingBay.CurrentSessionId, request.VehicleSessionId, StringComparison.OrdinalIgnoreCase)
                    && (existingBay.LeaseExpiresAtUtc is null || existingBay.LeaseExpiresAtUtc > now);
            }

            if (stillActive)
            {
                var existingStartNodeId = existing.Route.NodeIds.Count > 0
                    ? existing.Route.NodeIds[0]
                    : existing.LastKnownNodeId;

                if (string.Equals(existingStartNodeId, request.StartNodeId, StringComparison.OrdinalIgnoreCase) &&
                    existing.VehicleType == request.VehicleType)
                {
                    activity?.SetTag("parkmesh.idempotent_hit", true);
                    activity?.SetTag("parkmesh.allocated_bay", existing.AssignedBayId);
                    return new AllocateSpotResult(true, existing, ComputationTimeMs: sw.Elapsed.TotalMilliseconds);
                }

                return new AllocateSpotResult(
                    false,
                    FailureReason: $"Session '{request.VehicleSessionId}' already has an active lease on bay '{existing.AssignedBayId}'.",
                    ComputationTimeMs: sw.Elapsed.TotalMilliseconds);
            }
        }
        var ttl = request.LeaseTtl ?? DefaultLeaseTtl;
        HashSet<string>? exhaustedBays = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var congestionSnapshot = runtime.Congestion.Snapshot();
            var requestedCandidates = exhaustedBays is null
                ? initialCandidateBatchSize
                : Math.Min(runtime.Graph.BaysById.Count, exhaustedBays.Count + initialCandidateBatchSize);

            var astarSw = Stopwatch.StartNew();
            IReadOnlyList<ParkingRoute> candidates;
            lock (runtime.StateLock)
            {
                candidates = _pathfinder.FindCandidateBayRoutes(
                    runtime.Graph,
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
                lock (runtime.StateLock)
                {
                    var bay = runtime.Graph.BaysById[candidateRoute.TargetBayId];
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
                    expiresAt,
                    currentTenant);

                _sessions[sessionKey] = session;
                runtime.Congestion.AddRouteLoad(candidateRoute.Steps.Select(s => s.EdgeId));
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

            if (!attemptedNewCandidate || (exhaustedBays?.Count ?? 0) >= runtime.Graph.BaysById.Count)
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
        var currentTenant = _tenantContext.CurrentTenantId;
        var runtime = GetRuntime(currentTenant);

        if (!runtime.Graph.TryGetBay(bayId, out var bay))
            return false;

        var releasedInRedis = await _leaseManager.TryReleaseLeaseAsync(bayId, fencingToken, ct);
        if (!releasedInRedis)
            return false;

        ActiveVehicleSession? removedSession = null;
        lock (runtime.StateLock)
        {
            if (bay.CurrentSessionId is not null && bay.FencingToken == fencingToken)
            {
                _sessions.TryRemove(new SessionKey(currentTenant, bay.CurrentSessionId), out removedSession);
            }

            bay.TryReleaseLease(fencingToken);
        }

        if (removedSession is not null)
        {
            runtime.Congestion.RemoveRouteLoad(removedSession.Route.Steps.Select(s => s.EdgeId));
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
        var currentTenant = _tenantContext.CurrentTenantId;
        var runtime = GetRuntime(currentTenant);

        if (!runtime.Graph.TryGetBay(bayId, out var bay))
            throw new KeyNotFoundException($"Unknown bay '{bayId}'.");

        if (!occupied)
        {
            ActiveVehicleSession? departed = null;
            lock (runtime.StateLock)
            {
                if (bay.CurrentSessionId is not null)
                    _sessions.TryRemove(new SessionKey(currentTenant, bay.CurrentSessionId), out departed);

                bay.MarkFree();
            }

            await _leaseManager.InvalidateLeaseAsync(bayId, ct);

            if (departed is not null)
                runtime.Congestion.RemoveRouteLoad(departed.Route.Steps.Select(s => s.EdgeId));

            await _eventSink.OnPhysicalOccupancyChangedAsync(bayId, BayStatus.Free, ct);
            EnqueueAudit(
                _timeProvider.GetUtcNow(),
                AuditEventType.PhysicalVacated,
                bayId,
                departed?.VehicleSessionId,
                departed?.FencingToken,
                departed?.VehicleType,
                departed?.LastKnownNodeId,
                null,
                currentTenant);

            return new PhysicalOccupancyResult(bayId, BayStatus.Free, false, null, null);
        }

        ActiveVehicleSession? displacedSession = null;
        var wasIntruder = false;

        lock (runtime.StateLock)
        {
            if (bay.Status == BayStatus.Leased &&
                bay.CurrentSessionId is not null &&
                !string.Equals(bay.CurrentSessionId, occupantSessionId, StringComparison.OrdinalIgnoreCase))
            {
                wasIntruder = true;
                _sessions.TryRemove(new SessionKey(currentTenant, bay.CurrentSessionId), out displacedSession);
            }
            else if (bay.CurrentSessionId is not null &&
                     string.Equals(bay.CurrentSessionId, occupantSessionId, StringComparison.OrdinalIgnoreCase))
            {
                _sessions.TryRemove(new SessionKey(currentTenant, bay.CurrentSessionId), out var arrivedSession);
                if (arrivedSession is not null)
                    runtime.Congestion.RemoveRouteLoad(arrivedSession.Route.Steps.Select(s => s.EdgeId));
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

        var currentTenant = _tenantContext.CurrentTenantId;
        var runtime = GetRuntime(currentTenant);

        if (!runtime.Graph.TryGetBay(bayId, out var bay))
            throw new KeyNotFoundException($"Unknown bay '{bayId}'.");

        if (!outOfService)
        {
            var restored = false;
            BayStatus resultingStatus;
            lock (runtime.StateLock)
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

        lock (runtime.StateLock)
        {
            if (bay.Status == BayStatus.OutOfService)
                return new PhysicalOccupancyResult(bayId, BayStatus.OutOfService, false, null, null);

            if (bay.CurrentSessionId is not null)
                _sessions.TryRemove(new SessionKey(currentTenant, bay.CurrentSessionId), out displacedSession);

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
        var runtime = GetRuntime(displacedSession.TenantId);
        runtime.Congestion.RemoveRouteLoad(displacedSession.Route.Steps.Select(s => s.EdgeId));

        _logger.LogWarning(
            "{Reason} {BayId}! Displaced session {SessionId}; triggering live reroute from {LastNode}.",
            warningReason,
            bayId,
            displacedSession.VehicleSessionId,
            displacedSession.LastKnownNodeId);

        _metrics.RecordReroute();
        AllocateSpotResult rerouteResult;
        ActiveVehicleSession? newSession;
        using (_tenantContext.BeginScope(displacedSession.TenantId))
        {
            rerouteResult = await AllocateAsync(
                new AllocateSpotRequest(
                    displacedSession.LastKnownNodeId,
                    displacedSession.VehicleSessionId,
                    displacedSession.VehicleType),
                ct);

            newSession = rerouteResult.Succeeded ? rerouteResult.Session : null;
            await _eventSink.OnPhysicalOccupancyChangedAsync(bayId, resultingBayStatus, ct);
            await _eventSink.OnRouteReassignedAsync(bayId, displacedSession.VehicleSessionId, newSession, ct);
        }
        EnqueueAudit(
            _timeProvider.GetUtcNow(),
            auditEventType,
            bayId,
            displacedSession.VehicleSessionId,
            newSession?.FencingToken,
            displacedSession.VehicleType,
            displacedSession.LastKnownNodeId,
            newSession is not null ? rerouteSuccessDetails(newSession) : rerouteFailureDetails,
            displacedSession.TenantId);

        return new PhysicalOccupancyResult(
            bayId,
            resultingBayStatus,
            WasIntruder: wasIntruder,
            DisplacedSessionId: displacedSession.VehicleSessionId,
            ReroutedSession: newSession);
    }

    public bool UpdateVehicleProgress(string vehicleSessionId, string currentNodeId)
    {
        var currentTenant = _tenantContext.CurrentTenantId;
        var runtime = GetRuntime(currentTenant);
        if (!runtime.Graph.TryGetNode(currentNodeId, out _))
            return false;

        var key = new SessionKey(currentTenant, vehicleSessionId);
        if (!_sessions.TryGetValue(key, out var current))
            return false;

        _sessions[key] = current with { LastKnownNodeId = currentNodeId };
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

    public IReadOnlyCollection<ActiveVehicleSession> GetActiveSessions(string tenantId) =>
        _sessions.Values
            .Where(s => string.Equals(s.TenantId, tenantId, StringComparison.Ordinal))
            .ToArray();

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
            var key = new SessionKey(candidate.TenantId, candidate.VehicleSessionId);
            var runtime = GetRuntime(candidate.TenantId);
            lock (runtime.StateLock)
            {
                if (_sessions.TryGetValue(key, out var current)
                    && current.FencingToken == candidate.FencingToken
                    && current.ExpiresAtUtc <= now
                    && runtime.Graph.TryGetBay(current.AssignedBayId, out var bay)
                    && bay.TryReleaseLease(current.FencingToken))
                {
                    _sessions.TryRemove(key, out purged);
                }
            }

            if (purged is null)
                continue;

            runtime.Congestion.RemoveRouteLoad(purged.Route.Steps.Select(s => s.EdgeId));
            using (_tenantContext.BeginScope(purged.TenantId))
            {
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
                    null,
                    purged.TenantId);
            }

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

    public Task<int> RehydrateStateAsync(CancellationToken ct = default) =>
        RehydrateStateAsync([_tenantContext.CurrentTenantId], ct);

    public async Task<int> RehydrateStateAsync(IEnumerable<string> tenantIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenantIds);

        var distinctTenants = tenantIds
            .Where(TenantIdValidator.IsValid)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (distinctTenants.Length == 0)
            distinctTenants = [TenantDefaults.DefaultTenantId];

        var totalRestored = 0;
        var totalLeases = 0;
        var now = _timeProvider.GetUtcNow();

        foreach (var tenantId in distinctTenants)
        {
            ct.ThrowIfCancellationRequested();

            var runtime = GetRuntime(tenantId);
            var defaultGateId = runtime.Graph.Gates.FirstOrDefault()?.Id;
            var bayIds = runtime.Graph.BaysById.Keys.ToArray();

            IReadOnlyDictionary<string, AuditEventType> physicalStates;
            IReadOnlyList<ActiveLeaseSnapshot> activeLeases;
            IReadOnlyList<ParkingAuditEvent>? recentEvents = null;

            using (_tenantContext.BeginScope(tenantId))
            {
                physicalStates = await _auditStore.GetLatestBayPhysicalStatesAsync(tenantId, ct);
                activeLeases = await _leaseManager.GetActiveLeasesAsync(bayIds, ct);
                if (activeLeases.Count > 0)
                {
                    recentEvents = await _auditStore.GetRecentAsync(tenantId, 200, ct);
                }
            }

            Dictionary<(string BayId, long Token), ParkingAuditEvent>? allocationAuditByLease = null;
            if (recentEvents is not null)
            {
                allocationAuditByLease = new Dictionary<(string BayId, long Token), ParkingAuditEvent>();
                foreach (var ev in recentEvents)
                {
                    if (ev.EventType == AuditEventType.BayAllocated && ev.FencingToken.HasValue)
                    {
                        allocationAuditByLease.TryAdd((ev.BayId, ev.FencingToken.Value), ev);
                    }
                }
            }

            var precomputedRoutes = new List<(ActiveLeaseSnapshot Lease, ParkingRoute Route, VehicleType VehicleType, string StartNodeId)>(activeLeases.Count);

            foreach (var lease in activeLeases)
            {
                if (!runtime.Graph.TryGetBay(lease.BayId, out var bay))
                    continue;

                ParkingAuditEvent? allocEvent = null;
                allocationAuditByLease?.TryGetValue((lease.BayId, lease.FencingToken), out allocEvent);

                var startNodeId = allocEvent?.NodeId is not null && runtime.Graph.TryGetNode(allocEvent.NodeId, out _)
                    ? allocEvent.NodeId
                    : defaultGateId ?? bay.NodeId;

                var vehicleType = allocEvent?.VehicleType ?? bay.PrimaryType;
                var route = _pathfinder.FindShortestPath(runtime.Graph, startNodeId, bay.BayId)
                    ?? _pathfinder.FindShortestPath(runtime.Graph, bay.NodeId, bay.BayId);

                if (route is null)
                    continue;

                precomputedRoutes.Add((lease, route, vehicleType, startNodeId));
            }

            var restoredSessions = new List<ActiveVehicleSession>(precomputedRoutes.Count);
            var restoredForTenant = 0;

            lock (runtime.StateLock)
            {
                foreach (var (bayId, eventType) in physicalStates)
                {
                    if (!runtime.Graph.TryGetBay(bayId, out var bay))
                        continue;

                    switch (eventType)
                    {
                        case AuditEventType.PhysicalOccupied or AuditEventType.IntrusionRerouted:
                            if (bay.Status != BayStatus.Occupied)
                            {
                                bay.MarkOccupied();
                                restoredForTenant++;
                            }
                            break;

                        case AuditEventType.BayOutOfService:
                            if (bay.Status != BayStatus.OutOfService)
                            {
                                bay.MarkOutOfService();
                                restoredForTenant++;
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
                    if (!runtime.Graph.TryGetBay(lease.BayId, out var bay))
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
                        expiresAt,
                        tenantId);

                    _sessions[new SessionKey(tenantId, lease.VehicleSessionId)] = session;
                    restoredSessions.Add(session);
                    restoredForTenant++;
                }
            }

            foreach (var session in restoredSessions)
            {
                runtime.Congestion.AddRouteLoad(session.Route.Steps.Select(s => s.EdgeId));
            }

            totalRestored += restoredForTenant;
            totalLeases += restoredSessions.Count;
        }

        _logger.LogInformation(
            "Rehydrated garage state on startup across {TenantCount} tenant(s): {RestoredCount} bay states ({LeaseCount} active leases).",
            distinctTenants.Length,
            totalRestored,
            totalLeases);

        return totalRestored;
    }

    private void EnqueueAudit(
        DateTimeOffset occurredAtUtc,
        AuditEventType eventType,
        string bayId,
        string? sessionId,
        long? fencingToken,
        VehicleType? vehicleType,
        string? nodeId,
        string? details,
        string? tenantIdOverride = null)
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
            TenantId: tenantIdOverride ?? _tenantContext.CurrentTenantId));
    }
}
