using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ParkMesh.Application.Allocation;
using ParkMesh.Domain;

namespace ParkMesh.Benchmarks;

public static class LoadHarnessRunner
{
    private const char RecordSeparator = '\u001e';

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> RunAsync(string[] args)
    {
        var concurrency = ParseIntArg(args, "--concurrency", 16, 1, 128);
        var iterations = ParseIntArg(args, "--requests", 200, 10, 50_000);
        var baseUrl = ParseStringArg(args, "--baseUrl");
        var sloPath = ParseStringArg(args, "--slo");

        WebApplicationFactory<Program>? factory = null;
        HttpClient? sharedClient = null;
        WebSocket? ws = null;
        using var wsCts = new CancellationTokenSource();
        long signalrFrames = 0;

        try
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                {
                    builder.UseSolutionRelativeContentRoot("src/ParkMesh.Api");
                    builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
                    builder.ConfigureAppConfiguration((_, config) =>
                    {
                        config.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["Mqtt:Enabled"] = "false",
                            ["Security:EnableRateLimiting"] = "false"
                        });
                    });
                });

                sharedClient = factory.CreateClient();
                var wsClient = factory.Server.CreateWebSocketClient();
                ws = await wsClient.ConnectAsync(new Uri("ws://localhost/hubs/parking?tenantId=tenant-load-1"), wsCts.Token);
            }
            else
            {
                sharedClient = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
                var clientWs = new ClientWebSocket();
                var wsUri = new Uri(sharedClient.BaseAddress.ToString().Replace("http", "ws", StringComparison.OrdinalIgnoreCase) + "hubs/parking?tenantId=tenant-load-1");
                await clientWs.ConnectAsync(wsUri, wsCts.Token);
                ws = clientWs;
            }

            // Complete SignalR JSON handshake cleanly
            await SendFrameAsync(ws, """{"protocol":"json","version":1}""", wsCts.Token);
            _ = await ReceiveFrameTextAsync(ws, wsCts.Token);

            var readerTask = Task.Run(async () =>
            {
                var buffer = new byte[16 * 1024];
                var sb = new StringBuilder();
                while (!wsCts.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    try
                    {
                        var result = await ws.ReceiveAsync(buffer, wsCts.Token);
                        if (result.MessageType == WebSocketMessageType.Close)
                            break;

                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                        var raw = sb.ToString();
                        int idx;
                        while ((idx = raw.IndexOf(RecordSeparator)) >= 0)
                        {
                            var frame = raw[..idx];
                            if (frame.Contains("\"type\":1", StringComparison.Ordinal))
                            {
                                Interlocked.Increment(ref signalrFrames);
                            }
                            raw = raw[(idx + 1)..];
                        }
                        sb.Clear().Append(raw);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        break;
                    }
                }
            });

            var gates = new[] { "GATE-N1", "GATE-S1" };
            var tenants = new[] { "tenant-load-1", "tenant-load-2", "tenant-load-3", "tenant-load-4" };
            var types = new[] { VehicleType.Standard, VehicleType.EV, VehicleType.SUV, VehicleType.Moto, VehicleType.Pcd };

            var latenciesMs = new ConcurrentBag<double>();
            long httpRequests = 0;
            long allocSucceeded = 0;
            long idempotentVerified = 0;
            long conflicts = 0;
            long errors = 0;

            var swTotal = Stopwatch.StartNew();
            var nextIteration = 0;

            var workers = Enumerable.Range(0, concurrency).Select(async workerId =>
            {
                while (true)
                {
                    var idx = Interlocked.Increment(ref nextIteration) - 1;
                    if (idx >= iterations)
                        break;

                    var tenant = tenants[idx % tenants.Length];
                    var gate = gates[idx % gates.Length];
                    var vehicleType = types[idx % types.Length];
                    var sessionId = $"LOAD-{workerId:D2}-{idx:D5}";

                    // 1. Allocate
                    var t0 = Stopwatch.GetTimestamp();
                    using var allocReq = new HttpRequestMessage(HttpMethod.Post, "/api/parking/allocations")
                    {
                        Content = JsonContent.Create(new { GateId = gate, VehicleSessionId = sessionId, VehicleType = vehicleType }, options: JsonOptions)
                    };
                    allocReq.Headers.Add("X-Tenant-Id", tenant);

                    using var allocResp = await sharedClient.SendAsync(allocReq);
                    latenciesMs.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                    Interlocked.Increment(ref httpRequests);

                    if ((int)allocResp.StatusCode == 409)
                    {
                        Interlocked.Increment(ref conflicts);
                        continue;
                    }

                    if (!allocResp.IsSuccessStatusCode)
                    {
                        Interlocked.Increment(ref errors);
                        continue;
                    }

                    Interlocked.Increment(ref allocSucceeded);
                    var envelope = await allocResp.Content.ReadFromJsonAsync<AllocationEnvelope>(JsonOptions);
                    if (envelope?.Session is null)
                        continue;

                    // 2. Periodic idempotent retry verification
                    if (idx % 10 == 0)
                    {
                        var tRetry = Stopwatch.GetTimestamp();
                        using var retryReq = new HttpRequestMessage(HttpMethod.Post, "/api/parking/allocations")
                        {
                            Content = JsonContent.Create(new { GateId = gate, VehicleSessionId = sessionId, VehicleType = vehicleType }, options: JsonOptions)
                        };
                        retryReq.Headers.Add("X-Tenant-Id", tenant);
                        using var retryResp = await sharedClient.SendAsync(retryReq);
                        latenciesMs.Add(Stopwatch.GetElapsedTime(tRetry).TotalMilliseconds);
                        Interlocked.Increment(ref httpRequests);
                        if (retryResp.IsSuccessStatusCode)
                        {
                            var retryEnv = await retryResp.Content.ReadFromJsonAsync<AllocationEnvelope>(JsonOptions);
                            if (retryEnv?.Session?.AssignedBayId == envelope.Session.AssignedBayId)
                            {
                                Interlocked.Increment(ref idempotentVerified);
                            }
                        }
                    }

                    // 3. Progress update if route has intermediate nodes
                    if (envelope.Session.Route.NodeIds.Count > 1)
                    {
                        var tProg = Stopwatch.GetTimestamp();
                        using var progReq = new HttpRequestMessage(HttpMethod.Post, $"/api/parking/sessions/{Uri.EscapeDataString(sessionId)}/progress")
                        {
                            Content = JsonContent.Create(new { CurrentNodeId = envelope.Session.Route.NodeIds[1] }, options: JsonOptions)
                        };
                        progReq.Headers.Add("X-Tenant-Id", tenant);
                        using var progResp = await sharedClient.SendAsync(progReq);
                        latenciesMs.Add(Stopwatch.GetElapsedTime(tProg).TotalMilliseconds);
                        Interlocked.Increment(ref httpRequests);
                    }

                    // 4. Arrive and vacate so bay capacity recycles under continuous load
                    var tOcc = Stopwatch.GetTimestamp();
                    using var occReq = new HttpRequestMessage(HttpMethod.Post, $"/api/parking/bays/{Uri.EscapeDataString(envelope.Session.AssignedBayId)}/occupancy")
                    {
                        Content = JsonContent.Create(new { Occupied = true, OccupantSessionId = sessionId }, options: JsonOptions)
                    };
                    occReq.Headers.Add("X-Tenant-Id", tenant);
                    using (var occResp = await sharedClient.SendAsync(occReq))
                    {
                        latenciesMs.Add(Stopwatch.GetElapsedTime(tOcc).TotalMilliseconds);
                        Interlocked.Increment(ref httpRequests);
                    }

                    var tVac = Stopwatch.GetTimestamp();
                    using var vacReq = new HttpRequestMessage(HttpMethod.Post, $"/api/parking/bays/{Uri.EscapeDataString(envelope.Session.AssignedBayId)}/occupancy")
                    {
                        Content = JsonContent.Create(new { Occupied = false }, options: JsonOptions)
                    };
                    vacReq.Headers.Add("X-Tenant-Id", tenant);
                    using (var vacResp = await sharedClient.SendAsync(vacReq))
                    {
                        latenciesMs.Add(Stopwatch.GetElapsedTime(tVac).TotalMilliseconds);
                        Interlocked.Increment(ref httpRequests);
                    }
                }
            }).ToArray();

            await Task.WhenAll(workers);
            swTotal.Stop();

            await Task.Delay(150);
            wsCts.Cancel();
            try { await readerTask; } catch { }

            var sorted = latenciesMs.ToArray();
            Array.Sort(sorted);
            var elapsedSec = Math.Max(0.001, swTotal.Elapsed.TotalSeconds);
            var inv = CultureInfo.InvariantCulture;

            var p50Ms = sorted.Length > 0 ? Percentile(sorted, 0.50) : 0;
            var p95Ms = sorted.Length > 0 ? Percentile(sorted, 0.95) : 0;
            var p99Ms = sorted.Length > 0 ? Percentile(sorted, 0.99) : 0;
            var maxMs = sorted.Length > 0 ? sorted[^1] : 0;
            var throughputRps = httpRequests / elapsedSec;
            var deliveredFrames = Interlocked.Read(ref signalrFrames);

            Console.WriteLine("=== ParkMesh Concurrent HTTP + SignalR Load Report ===");
            Console.WriteLine($"Target        : {(string.IsNullOrWhiteSpace(baseUrl) ? "In-Process WebApplicationFactory" : baseUrl)}");
            Console.WriteLine($"Concurrency   : {concurrency} workers | Cycles requested: {iterations}");
            Console.WriteLine(string.Create(inv, $"Elapsed       : {swTotal.Elapsed.TotalMilliseconds:F1} ms"));
            Console.WriteLine(string.Create(inv, $"HTTP Requests : {httpRequests} ({throughputRps:F1} req/s)"));
            Console.WriteLine($"Allocations   : {allocSucceeded} ok | {idempotentVerified} idempotent retries | {conflicts} conflicts | {errors} errors");
            Console.WriteLine($"SignalR Deltas: {deliveredFrames} JSON invocation frames delivered");
            if (sorted.Length > 0)
            {
                Console.WriteLine(string.Create(
                    inv,
                    $"Latency (ms)  : min={sorted[0]:F2} | P50={p50Ms:F2} | P95={p95Ms:F2} | P99={p99Ms:F2} | max={maxMs:F2}"));
            }

            if (!string.IsNullOrWhiteSpace(sloPath))
            {
                var sloOk = await EvaluateSloAsync(
                    sloPath,
                    p95Ms,
                    p99Ms,
                    throughputRps,
                    allocSucceeded,
                    idempotentVerified,
                    conflicts,
                    errors,
                    deliveredFrames);
                if (!sloOk)
                    return 2;
            }

            return errors == 0 ? 0 : 1;
        }
        finally
        {
            ws?.Dispose();
            sharedClient?.Dispose();
            factory?.Dispose();
        }
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        var index = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private static int ParseIntArg(string[] args, string flag, int fallback, int min, int max)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out var parsed))
            {
                return Math.Clamp(parsed, min, max);
            }
        }
        return fallback;
    }

    private static string? ParseStringArg(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private static Task SendFrameAsync(WebSocket ws, string json, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(json + RecordSeparator), WebSocketMessageType.Text, true, ct);

    private static async Task<string> ReceiveFrameTextAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var res = await ws.ReceiveAsync(buffer, ct);
        return Encoding.UTF8.GetString(buffer, 0, res.Count);
    }

    private static async Task<bool> EvaluateSloAsync(
        string sloPath,
        double p95Ms,
        double p99Ms,
        double throughputRps,
        long allocSucceeded,
        long idempotentVerified,
        long conflicts,
        long errors,
        long signalrFrames)
    {
        if (!File.Exists(sloPath))
        {
            Console.Error.WriteLine($"SLO FAILED: Baseline file '{sloPath}' not found.");
            return false;
        }

        await using var stream = File.OpenRead(sloPath);
        var doc = await JsonSerializer.DeserializeAsync<SloBaselineFile>(stream, JsonOptions);
        if (doc?.CiGate is null)
        {
            Console.Error.WriteLine($"SLO FAILED: Missing 'ciGate' section in '{sloPath}'.");
            return false;
        }

        var g = doc.CiGate;
        var inv = CultureInfo.InvariantCulture;
        var failures = new List<string>();
        if (p95Ms > g.MaxP95Ms) failures.Add(string.Create(inv, $"P95 {p95Ms:F2} ms > limit {g.MaxP95Ms:F2} ms"));
        if (p99Ms > g.MaxP99Ms) failures.Add(string.Create(inv, $"P99 {p99Ms:F2} ms > limit {g.MaxP99Ms:F2} ms"));
        if (throughputRps < g.MinThroughputRps) failures.Add(string.Create(inv, $"Throughput {throughputRps:F1} req/s < limit {g.MinThroughputRps:F1} req/s"));
        if (allocSucceeded < g.MinAllocationsOk) failures.Add(string.Create(inv, $"AllocationsOk {allocSucceeded} < limit {g.MinAllocationsOk}"));
        if (idempotentVerified < g.MinIdempotentVerified) failures.Add(string.Create(inv, $"IdempotentVerified {idempotentVerified} < limit {g.MinIdempotentVerified}"));
        if (conflicts > g.MaxConflicts) failures.Add(string.Create(inv, $"Conflicts {conflicts} > limit {g.MaxConflicts}"));
        if (errors > g.MaxErrors) failures.Add(string.Create(inv, $"Errors {errors} > limit {g.MaxErrors}"));
        if (signalrFrames < g.MinSignalrFrames) failures.Add(string.Create(inv, $"SignalrFrames {signalrFrames} < limit {g.MinSignalrFrames}"));

        if (failures.Count > 0)
        {
            Console.Error.WriteLine("SLO GATE FAILED:");
            foreach (var f in failures)
                Console.Error.WriteLine($"  - {f}");
            return false;
        }

        Console.WriteLine(string.Create(
            inv,
            $"SLO Gate      : PASSED (P95={p95Ms:F2}<={g.MaxP95Ms:F2} ms, P99={p99Ms:F2}<={g.MaxP99Ms:F2} ms, RPS={throughputRps:F1}>={g.MinThroughputRps:F1}, conflicts={conflicts}, errors={errors})"));
        return true;
    }

    private sealed record AllocationEnvelope(ActiveVehicleSession? Session, double ComputationTimeMs);

    private sealed record SloBaselineFile(SloGateThresholds? CiGate);

    private sealed record SloGateThresholds(
        double MaxP95Ms,
        double MaxP99Ms,
        double MinThroughputRps,
        long MinAllocationsOk,
        long MinIdempotentVerified,
        long MaxConflicts,
        long MaxErrors,
        long MinSignalrFrames);
}
