using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using ParkMesh.Api.Security;
using ParkMesh.Application.Abstractions;
using ParkMesh.Application.Allocation;
using ParkMesh.Application.Telemetry;

namespace ParkMesh.Api.Realtime;

public sealed class ParkingHub : Hub
{
    public const string TenantItemKey = "TenantId";

    private readonly IParkingAllocationService _allocationService;
    private readonly IEngineMetricsRecorder _metrics;
    private readonly ITelemetryIngestionChannel _telemetryChannel;
    private readonly SignalRParkingEventSink _eventSink;
    private readonly ITenantContext _tenantContext;
    private readonly IOptionsMonitor<SecurityOptions> _securityOptions;

    public ParkingHub(
        IParkingAllocationService allocationService,
        IEngineMetricsRecorder metrics,
        ITelemetryIngestionChannel telemetryChannel,
        SignalRParkingEventSink eventSink,
        ITenantContext tenantContext,
        IOptionsMonitor<SecurityOptions> securityOptions)
    {
        _allocationService = allocationService;
        _metrics = metrics;
        _telemetryChannel = telemetryChannel;
        _eventSink = eventSink;
        _tenantContext = tenantContext;
        _securityOptions = securityOptions;
    }

    public override async Task OnConnectedAsync()
    {
        var tenantId = _tenantContext.CurrentTenantId;
        Context.Items[TenantItemKey] = tenantId;
        await Groups.AddToGroupAsync(Context.ConnectionId, SignalRParkingEventSink.GroupNameFor(tenantId));
        await base.OnConnectedAsync();
    }

    public object GetParkingSnapshot()
    {
        var tenantId = Context.Items[TenantItemKey] as string ?? _tenantContext.CurrentTenantId;
        using (_tenantContext.BeginScope(tenantId))
        {
            return SnapshotBuilder.Build(_allocationService, _metrics, _telemetryChannel, _eventSink, tenantId);
        }
    }

    public async Task<object> SubscribeTenant(string tenantId)
    {
        if (!TenantIdValidator.IsValid(tenantId))
            throw new HubException("Invalid tenant identifier.");

        var allowed = _securityOptions.CurrentValue.AllowedTenants;
        if (allowed is { Length: > 0 } && !allowed.Contains(tenantId, StringComparer.Ordinal))
            throw new HubException("Tenant is not authorized.");

        if (Context.Items.TryGetValue(TenantItemKey, out var previousObj) &&
            previousObj is string previousTenant &&
            !string.Equals(previousTenant, tenantId, StringComparison.Ordinal))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, SignalRParkingEventSink.GroupNameFor(previousTenant));
        }

        Context.Items[TenantItemKey] = tenantId;
        await Groups.AddToGroupAsync(Context.ConnectionId, SignalRParkingEventSink.GroupNameFor(tenantId));

        using (_tenantContext.BeginScope(tenantId))
        {
            return SnapshotBuilder.Build(_allocationService, _metrics, _telemetryChannel, _eventSink, tenantId);
        }
    }
}

public static class SnapshotBuilder
{
    public static object Build(
        IParkingAllocationService allocationService,
        IEngineMetricsRecorder metrics,
        ITelemetryIngestionChannel telemetryChannel,
        SignalRParkingEventSink eventSink,
        string tenantId = TenantDefaults.DefaultTenantId)
    {
        var graph = allocationService.Graph;
        var sessions = allocationService.GetActiveSessions(tenantId);
        var engineMetrics = metrics.BuildSnapshot(graph, sessions.Count, telemetryChannel.GetMetrics());

        return new
        {
            tenantId,
            floors = graph.MaxFloor - graph.MinFloor + 1,
            minFloor = graph.MinFloor,
            maxFloor = graph.MaxFloor,
            gates = graph.Gates.Select(g => new { g.Id, g.Floor, g.X, g.Y }),
            nodes = graph.Nodes.Values,
            edges = graph.Edges,
            bays = graph.BaysById.Values
                .OrderBy(b => b.BayId, StringComparer.OrdinalIgnoreCase)
                .Select(b => new
                {
                    b.BayId,
                    b.NodeId,
                    b.Floor,
                    b.X,
                    b.Y,
                    b.PrimaryType,
                    b.AllowedTypes,
                    b.Status,
                    b.CurrentSessionId,
                    b.FencingToken,
                    b.LeaseExpiresAtUtc
                }),
            activeSessions = sessions,
            ledPanels = eventSink.GetLedPanelStates(tenantId),
            metrics = engineMetrics
        };
    }
}
