using Microsoft.AspNetCore.SignalR;
using ParkMesh.Application.Allocation;
using ParkMesh.Application.Telemetry;

namespace ParkMesh.Api.Realtime;

public sealed class MetricsBroadcastHostedService : BackgroundService
{
    private readonly IHubContext<ParkingHub> _hubContext;
    private readonly IParkingAllocationService _allocationService;
    private readonly IEngineMetricsRecorder _metrics;
    private readonly ITelemetryIngestionChannel _telemetryChannel;

    public MetricsBroadcastHostedService(
        IHubContext<ParkingHub> hubContext,
        IParkingAllocationService allocationService,
        IEngineMetricsRecorder metrics,
        ITelemetryIngestionChannel telemetryChannel)
    {
        _hubContext = hubContext;
        _allocationService = allocationService;
        _metrics = metrics;
        _telemetryChannel = telemetryChannel;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var sessions = _allocationService.GetActiveSessions();
            var snapshot = _metrics.BuildSnapshot(
                _allocationService.Graph,
                sessions.Count,
                _telemetryChannel.GetMetrics());

            await _hubContext.Clients.All.SendAsync("MetricsTick", snapshot, stoppingToken);
        }
    }
}
