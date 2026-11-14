using Microsoft.Extensions.Logging;
using ParkMesh.Application.Allocation;

namespace ParkMesh.Application.Telemetry;

public interface ISensorTelemetryProcessor
{
    Task<SensorFilterOutcome> ProcessSingleAsync(BaySensorReading reading, CancellationToken ct = default);
    Task RunAsync(CancellationToken ct);
}

public sealed class SensorTelemetryProcessor : ISensorTelemetryProcessor
{
    private readonly ITelemetryIngestionChannel _channel;
    private readonly ISensorReadingFilter _filter;
    private readonly IParkingAllocationService _allocationService;
    private readonly ILogger<SensorTelemetryProcessor> _logger;

    public SensorTelemetryProcessor(
        ITelemetryIngestionChannel channel,
        ISensorReadingFilter filter,
        IParkingAllocationService allocationService,
        ILogger<SensorTelemetryProcessor> logger)
    {
        _channel = channel;
        _filter = filter;
        _allocationService = allocationService;
        _logger = logger;
    }

    public async Task<SensorFilterOutcome> ProcessSingleAsync(BaySensorReading reading, CancellationToken ct = default)
    {
        var outcome = _filter.Evaluate(reading);
        _channel.RecordOutcome(outcome);

        if (outcome != SensorFilterOutcome.ConfirmedTransition)
            return outcome;

        try
        {
            var result = await _allocationService.ReconcilePhysicalOccupancyAsync(
                reading.BayId,
                reading.Occupied,
                reading.VehicleSessionId,
                ct);

            if (result.WasIntruder)
            {
                _logger.LogWarning(
                    "Sensor transition confirmed intrusion on bay {BayId}; rerouted session {SessionId} to {NewBayId}.",
                    reading.BayId,
                    result.DisplacedSessionId,
                    result.ReroutedSession?.AssignedBayId ?? "NONE");
            }
        }
        catch (KeyNotFoundException ex)
        {
            _logger.LogWarning(ex, "Ignoring telemetry for unknown bay {BayId}.", reading.BayId);
        }

        return outcome;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await foreach (var reading in _channel.ReadAllAsync(ct))
        {
            try
            {
                await ProcessSingleAsync(reading, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error processing telemetry reading for bay {BayId}.", reading.BayId);
            }
        }
    }
}
