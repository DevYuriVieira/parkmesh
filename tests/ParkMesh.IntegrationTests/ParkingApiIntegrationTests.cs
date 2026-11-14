using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using ParkMesh.Application.Allocation;
using ParkMesh.Domain;

namespace ParkMesh.IntegrationTests;

public sealed class ParkingApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient _client;

    public ParkingApiIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task HealthAndTopology_ReturnExpectedGarageSummary()
    {
        var health = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        var topologyResponse = await _client.GetFromJsonAsync<TopologySummary>("/api/parking/topology", JsonOptions);
        Assert.NotNull(topologyResponse);
        Assert.Equal(3, topologyResponse.Floors);
        Assert.Equal(120, topologyResponse.TotalBays);
    }

    [Fact]
    public async Task AllocateIntrusionAndRelease_EndToEndHttpFlow_WorksCorrectly()
    {
        var allocateResponse = await _client.PostAsJsonAsync(
            "/api/parking/allocations",
            new AllocateSpotApiRequest("GATE-N1", "API-CAR-1", VehicleType.EV, 90),
            JsonOptions);

        Assert.Equal(HttpStatusCode.Created, allocateResponse.StatusCode);
        var created = await allocateResponse.Content.ReadFromJsonAsync<AllocationEnvelope>(JsonOptions);
        Assert.NotNull(created?.Session);

        var originalBay = created.Session.AssignedBayId;

        var occupancyResponse = await _client.PostAsJsonAsync(
            $"/api/parking/bays/{originalBay}/occupancy",
            new PhysicalOccupancyApiRequest(true, "INTRUDER-X"),
            JsonOptions);

        Assert.Equal(HttpStatusCode.OK, occupancyResponse.StatusCode);
        var reconciled = await occupancyResponse.Content.ReadFromJsonAsync<PhysicalOccupancyResult>(JsonOptions);
        Assert.NotNull(reconciled);
        Assert.True(reconciled.WasIntruder);
        Assert.Equal("API-CAR-1", reconciled.DisplacedSessionId);
        Assert.NotNull(reconciled.ReroutedSession);
        Assert.NotEqual(originalBay, reconciled.ReroutedSession.AssignedBayId);

        var deleteResponse = await _client.DeleteAsync(
            $"/api/parking/allocations/{reconciled.ReroutedSession.AssignedBayId}?fencingToken={reconciled.ReroutedSession.FencingToken}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
    }

    private sealed record TopologySummary(int Floors, int TotalNodes, int TotalEdges, int TotalBays);
    private sealed record AllocationEnvelope(ActiveVehicleSession Session, double ComputationTimeMs);
}
