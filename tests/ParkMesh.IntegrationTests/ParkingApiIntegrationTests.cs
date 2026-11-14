using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using ParkMesh.Application.Allocation;
using ParkMesh.Application.Audit;
using ParkMesh.Application.Telemetry;
using ParkMesh.Domain;

namespace ParkMesh.IntegrationTests;

public sealed class ParkingApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const char RecordSeparator = '\u001e';

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ParkingApiIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
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
    public async Task DashboardRootAndSnapshotAndMetrics_ReturnExpectedPayloads()
    {
        var rootResponse = await _client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, rootResponse.StatusCode);
        var html = await rootResponse.Content.ReadAsStringAsync();
        Assert.Contains("ParkMesh", html, StringComparison.Ordinal);

        using var snapshotDoc = await _client.GetFromJsonAsync<JsonDocument>("/api/parking/snapshot", JsonOptions);
        Assert.NotNull(snapshotDoc);
        Assert.Equal(3, snapshotDoc.RootElement.GetProperty("floors").GetInt32());
        Assert.Equal(120, snapshotDoc.RootElement.GetProperty("bays").GetArrayLength());

        var metrics = await _client.GetFromJsonAsync<EngineMetricsSnapshot>("/api/parking/metrics", JsonOptions);
        Assert.NotNull(metrics);
        Assert.Equal(120, metrics.TotalBays);
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

        await _client.PostAsJsonAsync(
            $"/api/parking/bays/{originalBay}/occupancy",
            new PhysicalOccupancyApiRequest(false, null),
            JsonOptions);
    }

    [Fact]
    public async Task ChaosBurstAndIntruder_CreateSessionsAndTriggerLiveReroute()
    {
        var burstResponse = await _client.PostAsync("/api/parking/chaos/burst?count=6", null);
        Assert.Equal(HttpStatusCode.OK, burstResponse.StatusCode);

        var burst = await burstResponse.Content.ReadFromJsonAsync<BurstEnvelope>(JsonOptions);
        Assert.NotNull(burst);
        Assert.Equal(6, burst.Requested);
        Assert.Equal(6, burst.Succeeded);
        Assert.Equal(0, burst.Failed);
        Assert.Equal(6, burst.Sessions.Count);

        var intruderResponse = await _client.PostAsync("/api/parking/chaos/intruder", null);
        Assert.Equal(HttpStatusCode.OK, intruderResponse.StatusCode);

        var intruder = await intruderResponse.Content.ReadFromJsonAsync<PhysicalOccupancyResult>(JsonOptions);
        Assert.NotNull(intruder);
        Assert.True(intruder.WasIntruder);
        Assert.NotNull(intruder.ReroutedSession);
        Assert.NotEqual(intruder.BayId, intruder.ReroutedSession.AssignedBayId);

        foreach (var session in burst.Sessions)
        {
            if (session.AssignedBayId != intruder.BayId)
            {
                await _client.DeleteAsync(
                    $"/api/parking/allocations/{session.AssignedBayId}?fencingToken={session.FencingToken}");
            }
        }

        await _client.DeleteAsync(
            $"/api/parking/allocations/{intruder.ReroutedSession.AssignedBayId}?fencingToken={intruder.ReroutedSession.FencingToken}");

        await _client.PostAsJsonAsync(
            $"/api/parking/bays/{intruder.BayId}/occupancy",
            new PhysicalOccupancyApiRequest(false, null),
            JsonOptions);
    }

    [Fact]
    public async Task SignalRHub_ConnectsReturnsSnapshotAndStreamsLiveDeltas()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var wsClient = _factory.Server.CreateWebSocketClient();
        var hubUri = new Uri("ws://localhost/hubs/parking");
        using var ws = await wsClient.ConnectAsync(hubUri, cts.Token);

        await SendSignalRMessageAsync(ws, """{"protocol":"json","version":1}""", cts.Token);
        var handshake = await ReceiveUntilAsync(ws, _ => true, cts.Token);
        Assert.NotNull(handshake);

        await SendSignalRMessageAsync(
            ws,
            """{"type":1,"invocationId":"snap-1","target":"GetParkingSnapshot","arguments":[]}""",
            cts.Token);

        using var snapshotCompletion = await ReceiveUntilAsync(
            ws,
            doc => doc.RootElement.TryGetProperty("type", out var t)
                && t.GetInt32() == 3
                && doc.RootElement.TryGetProperty("invocationId", out var id)
                && id.GetString() == "snap-1",
            cts.Token);

        Assert.Equal(3, snapshotCompletion.RootElement.GetProperty("result").GetProperty("floors").GetInt32());

        var sessionId = $"HUB-CAR-{Guid.NewGuid():N}"[..16];
        var allocateResponse = await _client.PostAsJsonAsync(
            "/api/parking/allocations",
            new AllocateSpotApiRequest("GATE-S1", sessionId, VehicleType.Standard, 90),
            JsonOptions,
            cts.Token);

        Assert.Equal(HttpStatusCode.Created, allocateResponse.StatusCode);
        var allocated = await allocateResponse.Content.ReadFromJsonAsync<AllocationEnvelope>(JsonOptions, cts.Token);
        Assert.NotNull(allocated?.Session);

        using var routeAssigned = await ReceiveUntilAsync(
            ws,
            doc => IsHubTarget(doc, "RouteAssigned")
                && doc.RootElement.GetProperty("arguments")[0].GetProperty("vehicleSessionId").GetString() == sessionId,
            cts.Token);

        Assert.Equal(
            allocated.Session.AssignedBayId,
            routeAssigned.RootElement.GetProperty("arguments")[0].GetProperty("assignedBayId").GetString());

        var intrusionResponse = await _client.PostAsJsonAsync(
            $"/api/parking/bays/{allocated.Session.AssignedBayId}/occupancy",
            new PhysicalOccupancyApiRequest(true, "INTRUDER-HUB"),
            JsonOptions,
            cts.Token);

        Assert.Equal(HttpStatusCode.OK, intrusionResponse.StatusCode);
        var intrusion = await intrusionResponse.Content.ReadFromJsonAsync<PhysicalOccupancyResult>(JsonOptions, cts.Token);
        Assert.NotNull(intrusion?.ReroutedSession);

        using var routeReassigned = await ReceiveUntilAsync(
            ws,
            doc => IsHubTarget(doc, "RouteReassigned")
                && doc.RootElement.GetProperty("arguments")[0].GetProperty("displacedSessionId").GetString() == sessionId,
            cts.Token);

        var reassignedPayload = routeReassigned.RootElement.GetProperty("arguments")[0];
        Assert.Equal(allocated.Session.AssignedBayId, reassignedPayload.GetProperty("displacedBayId").GetString());
        Assert.Equal(
            intrusion.ReroutedSession.AssignedBayId,
            reassignedPayload.GetProperty("newSession").GetProperty("assignedBayId").GetString());

        await _client.DeleteAsync(
            $"/api/parking/allocations/{intrusion.ReroutedSession.AssignedBayId}?fencingToken={intrusion.ReroutedSession.FencingToken}",
            cts.Token);

        await _client.PostAsJsonAsync(
            $"/api/parking/bays/{allocated.Session.AssignedBayId}/occupancy",
            new PhysicalOccupancyApiRequest(false, null),
            JsonOptions,
            cts.Token);
    }

    [Fact]
    public async Task TelemetryReadingsEndpoint_AcceptsReadingsAndUpdatesMetrics()
    {
        var reading = new BaySensorReading("F0-B30", true, 50.0, 1, DateTimeOffset.UtcNow);
        var postResponse = await _client.PostAsJsonAsync("/api/parking/telemetry/readings", reading, JsonOptions);
        Assert.Equal(HttpStatusCode.Accepted, postResponse.StatusCode);

        var metrics = await _client.GetFromJsonAsync<TelemetryMetricsSnapshot>("/api/parking/telemetry/metrics", JsonOptions);
        Assert.NotNull(metrics);
        Assert.True(metrics.EnqueuedCount >= 1);
    }

    [Fact]
    public async Task AuditEndpoint_RecordsAllocationIntrusionAndReleaseEvents()
    {
        var sessionId = $"AUDIT-{Guid.NewGuid():N}"[..14];
        var allocateResponse = await _client.PostAsJsonAsync(
            "/api/parking/allocations",
            new AllocateSpotApiRequest("GATE-N1", sessionId, VehicleType.Standard, 90),
            JsonOptions);

        Assert.Equal(HttpStatusCode.Created, allocateResponse.StatusCode);
        var created = await allocateResponse.Content.ReadFromJsonAsync<AllocationEnvelope>(JsonOptions);
        Assert.NotNull(created?.Session);

        var intruderResponse = await _client.PostAsJsonAsync(
            $"/api/parking/bays/{created.Session.AssignedBayId}/occupancy",
            new PhysicalOccupancyApiRequest(true, "INTRUDER-AUD"),
            JsonOptions);

        Assert.Equal(HttpStatusCode.OK, intruderResponse.StatusCode);
        var intruder = await intruderResponse.Content.ReadFromJsonAsync<PhysicalOccupancyResult>(JsonOptions);
        Assert.NotNull(intruder?.ReroutedSession);

        await _client.DeleteAsync(
            $"/api/parking/allocations/{intruder.ReroutedSession.AssignedBayId}?fencingToken={intruder.ReroutedSession.FencingToken}");

        await _client.PostAsJsonAsync(
            $"/api/parking/bays/{created.Session.AssignedBayId}/occupancy",
            new PhysicalOccupancyApiRequest(false, null),
            JsonOptions);

        AuditResponse? audit = null;
        for (var attempt = 0; attempt < 25; attempt++)
        {
            audit = await _client.GetFromJsonAsync<AuditResponse>("/api/parking/audit?limit=50", JsonOptions);
            if (audit is not null
                && audit.Events.Any(e => e.SessionId == sessionId && e.EventType == AuditEventType.IntrusionRerouted)
                && audit.Events.Any(e => e.SessionId == sessionId && e.EventType == AuditEventType.LeaseReleased))
            {
                break;
            }

            await Task.Delay(50);
        }

        Assert.NotNull(audit);
        Assert.Contains(audit.Events, e => e.SessionId == sessionId && e.EventType == AuditEventType.BayAllocated);
        Assert.Contains(audit.Events, e => e.SessionId == sessionId && e.EventType == AuditEventType.IntrusionRerouted);
        Assert.Contains(audit.Events, e => e.SessionId == sessionId && e.EventType == AuditEventType.LeaseReleased);
    }

    private static bool IsHubTarget(JsonDocument doc, string targetName) =>
        doc.RootElement.TryGetProperty("type", out var t)
        && t.GetInt32() == 1
        && doc.RootElement.TryGetProperty("target", out var target)
        && string.Equals(target.GetString(), targetName, StringComparison.Ordinal);

    private static async Task SendSignalRMessageAsync(WebSocket ws, string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json + RecordSeparator);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private static async Task<JsonDocument> ReceiveUntilAsync(
        WebSocket ws,
        Func<JsonDocument, bool> predicate,
        CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var pending = new StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            while (true)
            {
                var current = pending.ToString();
                var sepIndex = current.IndexOf(RecordSeparator);
                if (sepIndex < 0)
                    break;

                var frame = current[..sepIndex];
                pending.Remove(0, sepIndex + 1);

                if (string.IsNullOrWhiteSpace(frame))
                    continue;

                var doc = JsonDocument.Parse(frame);
                if (predicate(doc))
                    return doc;

                doc.Dispose();
            }

            var messageBuffer = new StringBuilder();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new InvalidOperationException("SignalR WebSocket closed before expected frame arrived.");

                messageBuffer.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            while (!result.EndOfMessage);

            pending.Append(messageBuffer);
        }

        throw new TimeoutException("Timed out waiting for matching SignalR frame.");
    }

    private sealed record TopologySummary(int Floors, int TotalNodes, int TotalEdges, int TotalBays);
    private sealed record AllocationEnvelope(ActiveVehicleSession Session, double ComputationTimeMs);
    private sealed record BurstEnvelope(int Requested, int Succeeded, int Failed, List<ActiveVehicleSession> Sessions);
    private sealed record AuditResponse(int Count, long DroppedCount, List<ParkingAuditEvent> Events);
}
