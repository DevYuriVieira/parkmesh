using ParkMesh.Application.Allocation;

namespace ParkMesh.Api.Realtime;

public sealed class LeaseExpirationSweeperHostedService : BackgroundService
{
    private readonly IParkingAllocationService _allocationService;
    private readonly ILogger<LeaseExpirationSweeperHostedService> _logger;

    public LeaseExpirationSweeperHostedService(
        IParkingAllocationService allocationService,
        ILogger<LeaseExpirationSweeperHostedService> logger)
    {
        _allocationService = allocationService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await _allocationService.PurgeExpiredSessionsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error while sweeping expired parking bay leases.");
            }
        }
    }
}
