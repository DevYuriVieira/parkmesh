using Microsoft.AspNetCore.SignalR;
using ParkMesh.Application.Allocation;
using ParkMesh.Application.Telemetry;

namespace ParkMesh.Api.Realtime;

public sealed class ParkingHub : Hub
{
    private readonly IParkingAllocationService _allocationService;
    private readonly IEngineMetricsRecorder _metrics;
    private readonly ITelemetryIngestionChannel _telemetryChannel;
    private readonly SignalRParkingEventSink _eventSink;

    public ParkingHub(
        IParkingAllocationService allocationService,
        IEngineMetricsRecorder metrics,
        ITelemetryIngestionChannel telemetryChannel,
        SignalRParkingEventSink eventSink)
    {
        _allocationService = allocationService;
        _metrics = metrics;
        _telemetryChannel = telemetryChannel;
        _eventSink = eventSink;
    }

    public object GetParkingSnapshot() =>
        SnapshotBuilder.Build(_allocationService, _metrics, _telemetryChannel, _eventSink);
}

public static class SnapshotBuilder
{
    public static object Build(
        IParkingAllocationService allocationService,
        IEngineMetricsRecorder metrics,
        ITelemetryIngestionChannel telemetryChannel,
        SignalRParkingEventSink eventSink)
    {
        var graph = allocationService.Graph;
        var sessions = allocationService.GetActiveSessions();
        var engineMetrics = metrics.BuildSnapshot(graph, sessions.Count, telemetryChannel.GetMetrics());

        return new
        {
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
            ledPanels = eventSink.GetLedPanelStates(),
            metrics = engineMetrics
        };
    }
}
