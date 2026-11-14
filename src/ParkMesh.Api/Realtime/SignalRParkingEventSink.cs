using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
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
    private readonly IHubContext<ParkingHub> _hubContext;
    private readonly ParkingGraph _graph;
    private readonly ConcurrentDictionary<string, LedPanelState> _ledPanels = new(StringComparer.OrdinalIgnoreCase);

    public SignalRParkingEventSink(IHubContext<ParkingHub> hubContext, ParkingGraph graph)
    {
        _hubContext = hubContext;
        _graph = graph;
    }

    public IReadOnlyCollection<LedPanelState> GetLedPanelStates() => _ledPanels.Values.ToArray();

    public async ValueTask OnBayAllocatedAsync(ActiveVehicleSession session, CancellationToken ct = default)
    {
        await _hubContext.Clients.All.SendAsync("RouteAssigned", session, ct);

        if (_graph.TryGetBay(session.AssignedBayId, out var bay))
        {
            await _hubContext.Clients.All.SendAsync("BayStateChanged", new
            {
                bayId = bay.BayId,
                status = bay.Status,
                sessionId = bay.CurrentSessionId,
                leaseExpiresAtUtc = bay.LeaseExpiresAtUtc
            }, ct);
        }

        await BroadcastLedDirectivesAsync(session.Route.LedDirectives, ct);
    }

    public async ValueTask OnRouteReassignedAsync(
        string displacedBayId,
        string displacedSessionId,
        ActiveVehicleSession? newSession,
        CancellationToken ct = default)
    {
        await _hubContext.Clients.All.SendAsync("RouteReassigned", new
        {
            displacedBayId,
            displacedSessionId,
            newSession
        }, ct);

        if (newSession is not null)
        {
            await BroadcastLedDirectivesAsync(newSession.Route.LedDirectives, ct);
        }
    }

    public async ValueTask OnPhysicalOccupancyChangedAsync(string bayId, BayStatus newStatus, CancellationToken ct = default)
    {
        _graph.TryGetBay(bayId, out var bay);
        await _hubContext.Clients.All.SendAsync("BayStateChanged", new
        {
            bayId,
            status = newStatus,
            sessionId = bay?.CurrentSessionId,
            leaseExpiresAtUtc = bay?.LeaseExpiresAtUtc
        }, ct);
    }

    public async ValueTask OnVehicleMovedAsync(string vehicleSessionId, string currentNodeId, CancellationToken ct = default)
    {
        if (!_graph.TryGetNode(currentNodeId, out var node))
            return;

        await _hubContext.Clients.All.SendAsync("VehicleMoved", new
        {
            vehicleSessionId,
            currentNodeId,
            node.Floor,
            node.X,
            node.Y
        }, ct);
    }

    private async Task BroadcastLedDirectivesAsync(IReadOnlyList<LedDirective> directives, CancellationToken ct)
    {
        if (directives.Count == 0)
            return;

        var now = DateTimeOffset.UtcNow;
        foreach (var directive in directives)
        {
            var state = new LedPanelState(directive.PanelId, directive.Floor, directive.Direction, now);
            _ledPanels[directive.PanelId] = state;
            await _hubContext.Clients.All.SendAsync("LedPanelUpdated", state, ct);
        }
    }
}
