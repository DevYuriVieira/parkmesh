using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ParkMesh.Application.Allocation;
using ParkMesh.Domain;

namespace ParkMesh.TelemetryWorker;

public sealed class ParkMeshApiOptions
{
    public const string SectionName = "ParkMeshApi";
    public string BaseUrl { get; set; } = "http://localhost:5000";
}

public sealed class RemoteEngineAllocationProxy : IParkingAllocationService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<RemoteEngineAllocationProxy> _logger;

    public RemoteEngineAllocationProxy(
        ParkingGraph graph,
        IOptions<ParkMeshApiOptions> apiOptions,
        ILogger<RemoteEngineAllocationProxy> logger)
    {
        Graph = graph;
        _logger = logger;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(apiOptions.Value.BaseUrl.TrimEnd('/') + "/")
        };
    }

    public ParkingGraph Graph { get; }

    public async Task<AllocateSpotResult> AllocateAsync(AllocateSpotRequest request, CancellationToken ct = default)
    {
        try
        {
            var payload = new
            {
                GateId = request.StartNodeId,
                request.VehicleSessionId,
                request.VehicleType,
                LeaseSeconds = request.LeaseTtl.HasValue ? (int?)Math.Ceiling(request.LeaseTtl.Value.TotalSeconds) : null
            };

            var response = await _httpClient.PostAsJsonAsync("api/parking/allocations", payload, JsonOptions, ct);
            if (!response.IsSuccessStatusCode)
            {
                return new AllocateSpotResult(false, FailureReason: $"Engine returned {(int)response.StatusCode}.");
            }

            var envelope = await response.Content.ReadFromJsonAsync<AllocationEnvelope>(JsonOptions, ct);
            return envelope?.Session is not null
                ? new AllocateSpotResult(true, envelope.Session, ComputationTimeMs: envelope.ComputationTimeMs)
                : new AllocateSpotResult(false, FailureReason: "Empty allocation response.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed forwarding gate allocation for {SessionId} to ParkMesh.Api.", request.VehicleSessionId);
            return new AllocateSpotResult(false, FailureReason: ex.Message);
        }
    }

    public async Task<bool> ReleaseLeaseAsync(string bayId, long fencingToken, CancellationToken ct = default)
    {
        var response = await _httpClient.DeleteAsync($"api/parking/allocations/{bayId}?fencingToken={fencingToken}", ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<PhysicalOccupancyResult> ReconcilePhysicalOccupancyAsync(
        string bayId,
        bool occupied,
        string? occupantSessionId = null,
        CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"api/parking/bays/{bayId}/occupancy",
            new { Occupied = occupied, OccupantSessionId = occupantSessionId },
            JsonOptions,
            ct);

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PhysicalOccupancyResult>(JsonOptions, ct);
        return result ?? new PhysicalOccupancyResult(bayId, occupied ? BayStatus.Occupied : BayStatus.Free, false, null, null);
    }

    public bool UpdateVehicleProgress(string vehicleSessionId, string currentNodeId) => false;

    public IReadOnlyCollection<ActiveVehicleSession> GetActiveSessions() => Array.Empty<ActiveVehicleSession>();

    public void Dispose() => _httpClient.Dispose();

    private sealed record AllocationEnvelope(ActiveVehicleSession Session, double ComputationTimeMs);
}
