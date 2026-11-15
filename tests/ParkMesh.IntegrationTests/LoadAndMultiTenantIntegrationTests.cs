using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using ParkMesh.Application.Allocation;
using ParkMesh.Application.Telemetry;
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
    public async Task SignalRHub_IsolatesBroadcastsPerTenantAndSupportsSubscribeTenant()
    {
        using var isolatedFactory = _factory.WithWebHostBuilder(builder =>
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

        using var client = isolatedFactory.CreateClient();
        var wsClient = isolatedFactory.Server.CreateWebSocketClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using var wsAlpha = await wsClient.ConnectAsync(new Uri("ws://localhost/hubs/parking?tenantId=tenant-alpha"), cts.Token);
        using var wsBeta = await wsClient.ConnectAsync(new Uri("ws://localhost/hubs/parking?tenantId=tenant-beta"), cts.Token);

        await SendFrameAsync(wsAlpha, """{"protocol":"json","version":1}""", cts.Token);
        await SendFrameAsync(wsBeta, """{"protocol":"json","version":1}""", cts.Token);
        _ = await ReceiveFrameTextAsync(wsAlpha, cts.Token);
        _ = await ReceiveFrameTextAsync(wsBeta, cts.Token);

        var alphaFrames = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var betaFrames = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var alphaReadTask = PumpFramesAsync(wsAlpha, alphaFrames, cts.Token);
        var betaReadTask = PumpFramesAsync(wsBeta, betaFrames, cts.Token);

        // A tenant only gets a runtime (and MetricsTick) once it is touched; touch beta so its tick isolation is asserted non-vacuously.
        using (var betaTouch = new HttpRequestMessage(HttpMethod.Get, "/api/parking/snapshot"))
        {
            betaTouch.Headers.Add("X-Tenant-Id", "tenant-beta");
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(betaTouch, cts.Token)).StatusCode);
        }

        using var allocAlpha = new HttpRequestMessage(HttpMethod.Post, "/api/parking/allocations")
        {
            Content = JsonContent.Create(new { GateId = "GATE-N1", VehicleSessionId = "SIG-ALPHA-1", VehicleType = VehicleType.Standard }, options: JsonOptions)
        };
        allocAlpha.Headers.Add("X-Tenant-Id", "tenant-alpha");
        var alphaResp = await client.SendAsync(allocAlpha, cts.Token);
        Assert.Equal(HttpStatusCode.Created, alphaResp.StatusCode);

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!alphaFrames.Any(f => f.Contains("\"RouteAssigned\"", StringComparison.Ordinal)) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25, cts.Token);
        }

        // Wait for at least one MetricsTick on the isolated tenant (1s broadcast interval) instead of a fixed sleep
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (!betaFrames.Any(f => f.Contains("\"MetricsTick\"", StringComparison.Ordinal)) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25, cts.Token);
        }
        Assert.Contains(betaFrames, f => f.Contains("\"MetricsTick\"", StringComparison.Ordinal));

        Assert.Contains(alphaFrames, f => f.Contains("\"RouteAssigned\"", StringComparison.Ordinal) && f.Contains("SIG-ALPHA-1", StringComparison.Ordinal));
        Assert.DoesNotContain(betaFrames, f => f.Contains("SIG-ALPHA-1", StringComparison.Ordinal));
        Assert.DoesNotContain(betaFrames, f => f.Contains("\"RouteAssigned\"", StringComparison.Ordinal));

        var betaMetrics = betaFrames.Where(f => f.Contains("\"MetricsTick\"", StringComparison.Ordinal)).ToArray();
        Assert.All(betaMetrics, frame =>
        {
            using var doc = JsonDocument.Parse(frame);
            var metrics = doc.RootElement.GetProperty("arguments")[0];
            Assert.Equal(0, metrics.GetProperty("activeSessions").GetInt32());
            Assert.Equal(0, metrics.GetProperty("leasedBays").GetInt32());
        });

        // SubscribeTenant rejects invalid tenant identifiers and allows valid group switching
        await SendFrameAsync(wsBeta, """{"type":1,"invocationId":"sub-bad","target":"SubscribeTenant","arguments":["../bad"]}""", cts.Token);
        deadline = DateTime.UtcNow.AddSeconds(3);
        while (!betaFrames.Any(f => f.Contains("\"sub-bad\"", StringComparison.Ordinal)) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25, cts.Token);
        }
        var badSub = betaFrames.First(f => f.Contains("\"sub-bad\"", StringComparison.Ordinal));
        Assert.Contains("\"error\"", badSub, StringComparison.Ordinal);

        await SendFrameAsync(wsBeta, """{"type":1,"invocationId":"sub-alpha","target":"SubscribeTenant","arguments":["tenant-alpha"]}""", cts.Token);
        deadline = DateTime.UtcNow.AddSeconds(3);
        while (!betaFrames.Any(f => f.Contains("\"sub-alpha\"", StringComparison.Ordinal)) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25, cts.Token);
        }
        var okSub = betaFrames.First(f => f.Contains("\"sub-alpha\"", StringComparison.Ordinal));
        Assert.Contains("\"tenant-alpha\"", okSub, StringComparison.Ordinal);

        cts.Cancel();
        try { await Task.WhenAll(alphaReadTask, betaReadTask); } catch { }
    }

    [Fact]
    public async Task ConcurrentMultiTenantWorkload_IsolatesSessionsLeasesAuditAndMetrics()
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

        // Each tenant owns an isolated virtual garage with 6 distinct bays and isolated metrics
        foreach (var tenant in tenants)
        {
            var tenantBays = allocated.Where(x => x.tenant == tenant).Select(x => x.Item2.AssignedBayId).ToArray();
            Assert.Equal(6, tenantBays.Distinct(StringComparer.OrdinalIgnoreCase).Count());

            using var sessReq = new HttpRequestMessage(HttpMethod.Get, "/api/parking/sessions");
            sessReq.Headers.Add("X-Tenant-Id", tenant);
            var sessResp = await client.SendAsync(sessReq);
            Assert.Equal(HttpStatusCode.OK, sessResp.StatusCode);
            var sessions = await sessResp.Content.ReadFromJsonAsync<List<ActiveVehicleSession>>(JsonOptions);
            Assert.NotNull(sessions);
            Assert.Equal(6, sessions!.Count);
            Assert.All(sessions, s => Assert.Equal(tenant, s.TenantId));

            using var metricsReq = new HttpRequestMessage(HttpMethod.Get, "/api/parking/metrics");
            metricsReq.Headers.Add("X-Tenant-Id", tenant);
            var metrics = await (await client.SendAsync(metricsReq)).Content.ReadFromJsonAsync<EngineMetricsSnapshot>(JsonOptions);
            Assert.NotNull(metrics);
            Assert.Equal(6, metrics!.ActiveSessions);
            Assert.Equal(6, metrics.LeasedBays);
        }

        // Allocate an EV bay solely in tenant-alpha; tenant-beta cannot release alpha's lease
        using var alphaOnlyReq = new HttpRequestMessage(HttpMethod.Post, "/api/parking/allocations")
        {
            Content = JsonContent.Create(new { GateId = "GATE-N1", VehicleSessionId = "ALPHA-ONLY-EV", VehicleType = VehicleType.EV }, options: JsonOptions)
        };
        alphaOnlyReq.Headers.Add("X-Tenant-Id", "tenant-alpha");
        var alphaOnlyResp = await client.SendAsync(alphaOnlyReq);
        Assert.Equal(HttpStatusCode.Created, alphaOnlyResp.StatusCode);
        var alphaOnlySession = (await alphaOnlyResp.Content.ReadFromJsonAsync<AllocationEnvelope>(JsonOptions))!.Session!;

        using (var crossDel = new HttpRequestMessage(
                   HttpMethod.Delete,
                   $"/api/parking/allocations/{alphaOnlySession.AssignedBayId}?fencingToken={alphaOnlySession.FencingToken}"))
        {
            crossDel.Headers.Add("X-Tenant-Id", "tenant-beta");
            var crossResp = await client.SendAsync(crossDel);
            Assert.Equal(HttpStatusCode.Conflict, crossResp.StatusCode);
        }

        // Owning tenant can release cleanly
        using (var ownerDel = new HttpRequestMessage(
                   HttpMethod.Delete,
                   $"/api/parking/allocations/{alphaOnlySession.AssignedBayId}?fencingToken={alphaOnlySession.FencingToken}"))
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
        Assert.Contains("SubscribeTenant", js);
        Assert.Contains("state.ledPanels.clear()", js);
    }

    [Fact]
    public async Task ProductionHardening_FailsFastWithoutApiKey_HidesChaosEndpoints_AndIsolatesChaosRateLimit()
    {
        using var insecureProd = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Security:ApiKey"] = "",
                    ["Security:AllowUnauthenticatedProduction"] = "false"
                });
            });
        });

        Assert.Throws<InvalidOperationException>(() => insecureProd.CreateClient());

        using var openTenantsProd = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Security:ApiKey"] = "prod-secret-key",
                    ["Mqtt:Enabled"] = "false"
                });
            });
        });

        // Without an explicit tenant allowlist every X-Tenant-Id would allocate a garage in memory.
        Assert.Throws<InvalidOperationException>(() => openTenantsProd.CreateClient());

        using var securedProd = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Security:ApiKey"] = "prod-secret-key",
                    ["Security:AllowedTenants:0"] = "default",
                    ["Security:EnableChaosEndpoints"] = "false",
                    ["Mqtt:Enabled"] = "false"
                });
            });
        });

        using var prodClient = securedProd.CreateClient();

        // Chaos endpoints are unmapped in Production by default (even with API key)
        using (var chaosReq = new HttpRequestMessage(HttpMethod.Post, "/api/parking/chaos/burst?count=1"))
        {
            chaosReq.Headers.Add("X-Api-Key", "prod-secret-key");
            Assert.Equal(HttpStatusCode.NotFound, (await prodClient.SendAsync(chaosReq)).StatusCode);
        }

        // Unauthenticated operational reads and hub upgrade are rejected with 401
        Assert.Equal(HttpStatusCode.Unauthorized, (await prodClient.GetAsync("/api/parking/snapshot")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await prodClient.GetAsync("/api/parking/metrics")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await prodClient.GetAsync("/api/parking/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await prodClient.GetAsync("/hubs/parking")).StatusCode);

        // Chaos rate limit is partitioned per tenant
        using var devRateLimitFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Security:EnableRateLimiting"] = "true",
                    ["Security:ChaosPermitLimit"] = "1",
                    ["Security:ChaosWindowSeconds"] = "60",
                    ["Mqtt:Enabled"] = "false"
                });
            });
        });

        using var rlClient = devRateLimitFactory.CreateClient();
        using var alphaBurst1 = new HttpRequestMessage(HttpMethod.Post, "/api/parking/chaos/burst?count=1");
        alphaBurst1.Headers.Add("X-Tenant-Id", "tenant-alpha");
        Assert.Equal(HttpStatusCode.OK, (await rlClient.SendAsync(alphaBurst1)).StatusCode);

        using var alphaBurst2 = new HttpRequestMessage(HttpMethod.Post, "/api/parking/chaos/burst?count=1");
        alphaBurst2.Headers.Add("X-Tenant-Id", "tenant-alpha");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await rlClient.SendAsync(alphaBurst2)).StatusCode);

        using var betaBurst = new HttpRequestMessage(HttpMethod.Post, "/api/parking/chaos/burst?count=1");
        betaBurst.Headers.Add("X-Tenant-Id", "tenant-beta");
        Assert.Equal(HttpStatusCode.OK, (await rlClient.SendAsync(betaBurst)).StatusCode);
    }

    private static async Task PumpFramesAsync(
        WebSocket ws,
        System.Collections.Concurrent.ConcurrentQueue<string> sink,
        CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        var sb = new StringBuilder();
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            try
            {
                var res = await ws.ReceiveAsync(buf, ct);
                if (res.MessageType == WebSocketMessageType.Close) break;
                sb.Append(Encoding.UTF8.GetString(buf, 0, res.Count));
                var text = sb.ToString();
                int idx;
                while ((idx = text.IndexOf(RecordSeparator)) >= 0)
                {
                    sink.Enqueue(text[..idx]);
                    text = text[(idx + 1)..];
                }
                sb.Clear().Append(text);
            }
            catch
            {
                break;
            }
        }
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
