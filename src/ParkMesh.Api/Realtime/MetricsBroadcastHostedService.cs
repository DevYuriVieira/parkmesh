using Microsoft.AspNetCore.SignalR;
using ParkMesh.Application.Abstractions;
using ParkMesh.Application.Allocation;
using ParkMesh.Application.Telemetry;

namespace ParkMesh.Api.Realtime;

public sealed class MetricsBroadcastHostedService : BackgroundService
{
    private readonly IHubContext<ParkingHub> _hubContext;
    private readonly IParkingAllocationService _allocationService;
    private readonly IEngineMetricsRecorder _metrics;
    private readonly ITelemetryIngestionChannel _telemetryChannel;
    private readonly ITenantContext _tenantContext;

    public MetricsBroadcastHostedService(
        IHubContext<ParkingHub> hubContext,
        IParkingAllocationService allocationService,
        IEngineMetricsRecorder metrics,
        ITelemetryIngestionChannel telemetryChannel,
        ITenantContext tenantContext)
    {
        _hubContext = hubContext;
        _allocationService = allocationService;
        _metrics = metrics;
        _telemetryChannel = telemetryChannel;
        _tenantContext = tenantContext;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var telemetry = _telemetryChannel.GetMetrics();
            foreach (var tenantId in _allocationService.GetKnownTenants())
            {
                using (_tenantContext.BeginScope(tenantId))
                {
                    var sessions = _allocationService.GetActiveSessions(tenantId);
                    var snapshot = _metrics.BuildSnapshot(
                        _allocationService.Graph,
                        sessions.Count,
                        telemetry);

                    await _hubContext.Clients
                        .Group(SignalRParkingEventSink.GroupNameFor(tenantId))
                        .SendAsync("MetricsTick", snapshot, stoppingToken);
                }
            }
        }
    }
}
