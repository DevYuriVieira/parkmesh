using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using ParkMesh.Application.Allocation;
using ParkMesh.Domain;

namespace ParkMesh.IntegrationTests;

public sealed class LoadAndMultiTenantIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const char RecordSeparator = '\u001e';

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly WebApplicationFactory<Program> _factory;

    public LoadAndMultiTenantIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ConcurrentMultiTenantWorkload_IsolatesSessionsLeasesAuditAndStreamsSignalR()
    {
        using var loadFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Mqtt:Enabled"] = "false",
                    ["Security:EnableRateLimiting"] = "false"
                });
            });
        });

        using var client = loadFactory.CreateClient();
        var wsClient = loadFactory.Server.CreateWebSocketClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var ws = await wsClient.ConnectAsync(new Uri("ws://localhost/hubs/parking"), cts.Token);

        await SendFrameAsync(ws, """{"protocol":"json","version":1}""", cts.Token);
        _ = await ReceiveFrameTextAsync(ws, cts.Token);

        var signalrInvocations = 0;
        var receiveTask = Task.Run(async () =>
        {
            var buf = new byte[16 * 1024];
            var sb = new StringBuilder();
            while (!cts.Token.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                try
                {
                    var res = await ws.ReceiveAsync(buf, cts.Token);
                    if (res.MessageType == WebSocketMessageType.Close) break;
                    sb.Append(Encoding.UTF8.GetString(buf, 0, res.Count));
                    var text = sb.ToString();
                    int idx;
                    while ((idx = text.IndexOf(RecordSeparator)) >= 0)
                    {
                        var frame = text[..idx];
                        if (frame.Contains("\"type\":1", StringComparison.Ordinal))
                        {
                            Interlocked.Increment(ref signalrInvocations);
                        }
                        text = text[(idx + 1)..];
                    }
                    sb.Clear().Append(text);
                }
                catch
                {
                    break;
                }
            }
        });

        var tenants = new[] { "tenant-alpha", "tenant-beta", "tenant-gamma" };
        var allocTasks = Enumerable.Range(0, 18).Select(async i =>
        {
            var tenant = tenants[i % tenants.Length];
            var sessionId = $"SHARED-VIP-{i / tenants.Length:D2}";
            var gate = i % 2 == 0 ? "GATE-N1" : "GATE-S1";

            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/parking/allocations")
            {
                Content = JsonContent.Create(new { GateId = gate, VehicleSessionId = sessionId, VehicleType = VehicleType.Standard }, options: JsonOptions)
            };
            req.Headers.Add("X-Tenant-Id", tenant);

            var resp = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            var payload = await resp.Content.ReadFromJsonAsync<AllocationEnvelope>(JsonOptions);
            Assert.NotNull(payload?.Session);
            return (tenant, payload!.Session!);
        });

        var allocated = await Task.WhenAll(allocTasks);
        Assert.Equal(18, allocated.Select(a => a.Item2.AssignedBayId).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // Each tenant sees only its own 6 sessions
        foreach (var tenant in tenants)
        {
            using var sessReq = new HttpRequestMessage(HttpMethod.Get, "/api/parking/sessions");
            sessReq.Headers.Add("X-Tenant-Id", tenant);
            var sessResp = await client.SendAsync(sessReq);
            Assert.Equal(HttpStatusCode.OK, sessResp.StatusCode);
            var sessions = await sessResp.Content.ReadFromJsonAsync<List<ActiveVehicleSession>>(JsonOptions);
            Assert.NotNull(sessions);
            Assert.Equal(6, sessions!.Count);
            Assert.All(sessions, s => Assert.Equal(tenant, s.TenantId));
        }

        // Cross-tenant lease release is blocked with 409 Conflict
        var alphaSession = allocated.First(x => x.tenant == "tenant-alpha").Item2;
        using (var crossDel = new HttpRequestMessage(
                   HttpMethod.Delete,
                   $"/api/parking/allocations/{alphaSession.AssignedBayId}?fencingToken={alphaSession.FencingToken}"))
        {
            crossDel.Headers.Add("X-Tenant-Id", "tenant-beta");
            var crossResp = await client.SendAsync(crossDel);
            Assert.Equal(HttpStatusCode.Conflict, crossResp.StatusCode);
        }

        // Owning tenant can release cleanly
        using (var ownerDel = new HttpRequestMessage(
                   HttpMethod.Delete,
                   $"/api/parking/allocations/{alphaSession.AssignedBayId}?fencingToken={alphaSession.FencingToken}"))
        {
            ownerDel.Headers.Add("X-Tenant-Id", "tenant-alpha");
            var ownerResp = await client.SendAsync(ownerDel);
            Assert.Equal(HttpStatusCode.NoContent, ownerResp.StatusCode);
        }

        // Verify dashboard UI serves tenant switcher controls
        var html = await client.GetStringAsync("/");
        Assert.Contains("id=\"tenantSelect\"", html);
        Assert.Contains("id=\"auditTenantLabel\"", html);
        var js = await client.GetStringAsync("/app.js");
        Assert.Contains("X-Tenant-Id", js);

        await Task.Delay(150);
        cts.Cancel();
        try { await receiveTask; } catch { }

        Assert.True(Volatile.Read(ref signalrInvocations) >= 18);
    }

    private static Task SendFrameAsync(WebSocket ws, string json, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(json + RecordSeparator), WebSocketMessageType.Text, true, ct);

    private static async Task<string> ReceiveFrameTextAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var res = await ws.ReceiveAsync(buffer, ct);
        return Encoding.UTF8.GetString(buffer, 0, res.Count);
    }

    private sealed record AllocationEnvelope(ActiveVehicleSession? Session, double ComputationTimeMs);
}
