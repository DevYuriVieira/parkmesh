using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ParkMesh.Application.Audit;

namespace ParkMesh.Infrastructure.Audit;

public sealed class ParkingAuditHostedService : BackgroundService
{
    private const int MaxBatchSize = 64;

    private readonly IParkingAuditSink _sink;
    private readonly IParkingAuditStore _store;
    private readonly ILogger<ParkingAuditHostedService> _logger;

    public ParkingAuditHostedService(
        IParkingAuditSink sink,
        IParkingAuditStore store,
        ILogger<ParkingAuditHostedService> logger)
    {
        _sink = sink;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _store.InitializeAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Failed to initialize parking audit store schema.");
        }

        var batch = new List<ParkingAuditEvent>(MaxBatchSize);

        try
        {
            while (await _sink.WaitToReadAsync(stoppingToken))
            {
                while (batch.Count < MaxBatchSize && _sink.TryRead(out var auditEvent))
                {
                    batch.Add(auditEvent);
                }

                if (batch.Count > 0)
                {
                    await FlushAsync(batch, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            while (batch.Count < MaxBatchSize && _sink.TryRead(out var remaining))
            {
                batch.Add(remaining);
            }

            if (batch.Count > 0)
            {
                await FlushAsync(batch, CancellationToken.None);
            }
        }
    }

    private async Task FlushAsync(List<ParkingAuditEvent> batch, CancellationToken ct)
    {
        try
        {
            await _store.RecordBatchAsync(batch, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Failed to persist {BatchCount} parking audit events.", batch.Count);
        }
        finally
        {
            batch.Clear();
        }
    }
}
