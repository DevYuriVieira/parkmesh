using ParkMesh.Application.Allocation;
using ParkMesh.Application.Audit;

namespace ParkMesh.Api.Realtime;

public sealed class GarageStateHydrationHostedService : BackgroundService
{
    private readonly IParkingAuditStore _auditStore;
    private readonly IParkingAllocationService _allocationService;
    private readonly IStateHydrationTracker _tracker;
    private readonly ILogger<GarageStateHydrationHostedService> _logger;

    public GarageStateHydrationHostedService(
        IParkingAuditStore auditStore,
        IParkingAllocationService allocationService,
        IStateHydrationTracker tracker,
        ILogger<GarageStateHydrationHostedService> logger)
    {
        _auditStore = auditStore;
        _allocationService = allocationService;
        _tracker = tracker;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _auditStore.InitializeAsync(stoppingToken);
                var restored = await _allocationService.RehydrateStateAsync(stoppingToken);
                _tracker.MarkHydrated(restored);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _tracker.MarkFailed(ex.Message);
                _logger.LogWarning(ex, "Startup state hydration failed; retrying in 1s before marking /health/ready.");

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
