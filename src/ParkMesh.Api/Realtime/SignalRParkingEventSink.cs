using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using ParkMesh.Application.Abstractions;
using ParkMesh.Application.Allocation;
using ParkMesh.Application.Telemetry;
using ParkMesh.Domain;

namespace ParkMesh.Api.Realtime;

public sealed record LedPanelState(
    string PanelId,
    int Floor,
    DirectionHint Direction,
    DateTimeOffset UpdatedAtUtc);

public sealed class SignalRParkingEventSink : IParkingEventSink
{
    private readonly record struct LedPanelKey(string TenantId, string PanelId);

    private sealed class LedPanelKeyComparer : IEqualityComparer<LedPanelKey>
    {
        public static readonly LedPanelKeyComparer Instance = new();

        public bool Equals(LedPanelKey x, LedPanelKey y) =>
            string.Equals(x.TenantId, y.TenantId, StringComparison.Ordinal) &&
            string.Equals(x.PanelId, y.PanelId, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(LedPanelKey obj)
        {
            var hc = new HashCode();
            hc.Add(obj.TenantId, StringComparer.Ordinal);
            hc.Add(obj.PanelId, StringComparer.OrdinalIgnoreCase);
            return hc.ToHashCode();
        }
    }

    private readonly IHubContext<ParkingHub> _hubContext;
    private readonly ParkingGraph _graph;
    private readonly ITenantContext _tenantContext;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<LedPanelKey, LedPanelState> _ledPanels = new(LedPanelKeyComparer.Instance);

    public SignalRParkingEventSink(
        IHubContext<ParkingHub> hubContext,
        ParkingGraph graph,
        ITenantContext tenantContext,
        TimeProvider? timeProvider = null)
    {
        _hubContext = hubContext;
        _graph = graph;
        _tenantContext = tenantContext;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static string GroupNameFor(string tenantId) => $"tenant:{tenantId}";

    public IReadOnlyCollection<LedPanelState> GetLedPanelStates() =>
        GetLedPanelStates(_tenantContext.CurrentTenantId);

    public IReadOnlyCollection<LedPanelState> GetLedPanelStates(string tenantId) =>
        _ledPanels
            .Where(kvp => string.Equals(kvp.Key.TenantId, tenantId, StringComparison.Ordinal))
            .Select(kvp => kvp.Value)
            .ToArray();

    public async ValueTask OnBayAllocatedAsync(ActiveVehicleSession session, CancellationToken ct = default)
    {
        var tenantId = session.TenantId;
        var group = _hubContext.Clients.Group(GroupNameFor(tenantId));

        await group.SendAsync("RouteAssigned", session, ct);
        await group.SendAsync("BayStateChanged", new
        {
            bayId = session.AssignedBayId,
            status = BayStatus.Leased,
            sessionId = session.VehicleSessionId,
            leaseExpiresAtUtc = (DateTimeOffset?)session.ExpiresAtUtc
        }, ct);

        await BroadcastLedDirectivesAsync(tenantId, session.Route.LedDirectives, ct);
    }

    public async ValueTask OnRouteReassignedAsync(
        string displacedBayId,
        string displacedSessionId,
        ActiveVehicleSession? newSession,
        CancellationToken ct = default)
    {
        var tenantId = newSession?.TenantId ?? _tenantContext.CurrentTenantId;
        var group = _hubContext.Clients.Group(GroupNameFor(tenantId));

        await group.SendAsync("RouteReassigned", new
        {
            displacedBayId,
            displacedSessionId,
            newSession
        }, ct);

        if (newSession is not null)
        {
            await BroadcastLedDirectivesAsync(tenantId, newSession.Route.LedDirectives, ct);
        }
    }

    public async ValueTask OnPhysicalOccupancyChangedAsync(string bayId, BayStatus newStatus, CancellationToken ct = default)
    {
        var tenantId = _tenantContext.CurrentTenantId;
        await _hubContext.Clients.Group(GroupNameFor(tenantId)).SendAsync("BayStateChanged", new
        {
            bayId,
            status = newStatus,
            sessionId = (string?)null,
            leaseExpiresAtUtc = (DateTimeOffset?)null
        }, ct);
    }

    public async ValueTask OnVehicleMovedAsync(string vehicleSessionId, string currentNodeId, CancellationToken ct = default)
    {
        if (!_graph.TryGetNode(currentNodeId, out var node))
            return;

        var tenantId = _tenantContext.CurrentTenantId;
        await _hubContext.Clients.Group(GroupNameFor(tenantId)).SendAsync("VehicleMoved", new
        {
            vehicleSessionId,
            currentNodeId,
            node.Floor,
            node.X,
            node.Y
        }, ct);
    }

    private async Task BroadcastLedDirectivesAsync(string tenantId, IReadOnlyList<LedDirective> directives, CancellationToken ct)
    {
        if (directives.Count == 0)
            return;

        var now = _timeProvider.GetUtcNow();
        var group = _hubContext.Clients.Group(GroupNameFor(tenantId));
        foreach (var directive in directives)
        {
            var state = new LedPanelState(directive.PanelId, directive.Floor, directive.Direction, now);
            _ledPanels[new LedPanelKey(tenantId, directive.PanelId)] = state;
            await group.SendAsync("LedPanelUpdated", state, ct);
        }
    }
}
