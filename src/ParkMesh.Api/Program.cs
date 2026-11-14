using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ParkMesh.Api.Realtime;
using ParkMesh.Api.Security;
using ParkMesh.Application.Abstractions;
using ParkMesh.Application.Allocation;
using ParkMesh.Application.Audit;
using ParkMesh.Application.Telemetry;
using ParkMesh.Domain;
using ParkMesh.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection(SecurityOptions.SectionName));

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { error = "Rate limit exceeded on chaos endpoints." },
            token);
    };

    options.AddPolicy("chaos", httpContext =>
    {
        var security = httpContext.RequestServices.GetRequiredService<IOptionsMonitor<SecurityOptions>>().CurrentValue;
        if (!security.EnableRateLimiting)
            return RateLimitPartition.GetNoLimiter("disabled");

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: "chaos-global",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, security.ChaosPermitLimit),
                Window = TimeSpan.FromSeconds(Math.Max(1, security.ChaosWindowSeconds)),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            });
    });
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services
    .AddSignalR()
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

builder.Services.AddParkMeshCore();
builder.Services.AddParkMeshInfrastructure(builder.Configuration);
builder.Services.AddParkMeshTelemetry(builder.Configuration);

builder.Services.AddSingleton<SignalRParkingEventSink>();
builder.Services.AddSingleton<IParkingEventSink>(sp => sp.GetRequiredService<SignalRParkingEventSink>());
builder.Services.AddHostedService<MetricsBroadcastHostedService>();
builder.Services.AddHostedService<LeaseExpirationSweeperHostedService>();

var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
builder.Services
    .AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("ParkMesh.Api"))
    .WithTracing(tracing =>
    {
        tracing
            .AddSource(EngineMetricsRecorder.ActivitySourceName)
            .AddAspNetCoreInstrumentation()
            .AddNpgsql();

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            tracing.AddOtlpExporter();
    })
    .WithMetrics(metrics =>
    {
        metrics
            .AddMeter(EngineMetricsRecorder.MeterName)
            .AddAspNetCoreInstrumentation();

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
            metrics.AddOtlpExporter();
    });

var app = builder.Build();

app.UseRateLimiter();

var webRoot = ResolveWebDirectory(app.Environment.ContentRootPath);
if (webRoot is not null)
{
    var fileProvider = new PhysicalFileProvider(webRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = fileProvider });
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "ParkMesh.Api" }));

app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "ParkMesh.Api" }));

app.MapGet("/health/ready", async (
    IBayLeaseManager leaseManager,
    IParkingAuditStore auditStore,
    CancellationToken ct) =>
{
    var leaseHealthy = await leaseManager.PingAsync(ct);
    var auditHealthy = await auditStore.CheckConnectivityAsync(ct);
    var ready = leaseHealthy && auditHealthy;

    var payload = new
    {
        status = ready ? "ready" : "degraded",
        dependencies = new
        {
            leaseStore = leaseHealthy ? "healthy" : "unhealthy",
            auditStore = auditHealthy ? "healthy" : "unhealthy"
        }
    };

    return ready
        ? Results.Ok(payload)
        : Results.Json(payload, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.MapGet("/api/parking/topology", (IParkingAllocationService allocationService) =>
{
    var graph = allocationService.Graph;
    return Results.Ok(new
    {
        floors = graph.MaxFloor - graph.MinFloor + 1,
        minFloor = graph.MinFloor,
        maxFloor = graph.MaxFloor,
        totalNodes = graph.Nodes.Count,
        totalEdges = graph.Edges.Count,
        totalBays = graph.BaysById.Count,
        gates = graph.Gates.Select(g => new { g.Id, g.Floor, g.X, g.Y }),
        nodes = graph.Nodes.Values,
        edges = graph.Edges
    });
});

app.MapGet("/api/parking/snapshot", (
    IParkingAllocationService allocationService,
    IEngineMetricsRecorder metrics,
    ITelemetryIngestionChannel telemetryChannel,
    SignalRParkingEventSink eventSink) =>
    Results.Ok(SnapshotBuilder.Build(allocationService, metrics, telemetryChannel, eventSink)));

app.MapGet("/api/parking/metrics", (
    IParkingAllocationService allocationService,
    IEngineMetricsRecorder metrics,
    ITelemetryIngestionChannel telemetryChannel) =>
{
    var sessions = allocationService.GetActiveSessions();
    return Results.Ok(metrics.BuildSnapshot(allocationService.Graph, sessions.Count, telemetryChannel.GetMetrics()));
});

app.MapGet("/api/parking/audit", async (
    [FromQuery] int? limit,
    IParkingAuditStore auditStore,
    IParkingAuditSink auditSink,
    CancellationToken ct) =>
{
    var items = await auditStore.GetRecentAsync(limit ?? 50, ct);
    return Results.Ok(new
    {
        count = items.Count,
        droppedCount = auditSink.DroppedCount,
        events = items
    });
});

app.MapGet("/api/parking/bays", (
    IParkingAllocationService allocationService,
    [FromQuery] int? floor,
    [FromQuery] BayStatus? status,
    [FromQuery] VehicleType? vehicleType) =>
{
    var bays = allocationService.Graph.BaysById.Values.AsEnumerable();

    if (floor.HasValue)
        bays = bays.Where(b => b.Floor == floor.Value);
    if (status.HasValue)
        bays = bays.Where(b => b.Status == status.Value);
    if (vehicleType.HasValue)
        bays = bays.Where(b => b.SupportsVehicle(vehicleType.Value));

    var result = bays
        .OrderBy(b => b.BayId, StringComparer.OrdinalIgnoreCase)
        .Select(b => new
        {
            b.BayId,
            b.Floor,
            b.X,
            b.Y,
            b.PrimaryType,
            b.AllowedTypes,
            b.Status,
            b.CurrentSessionId,
            b.FencingToken,
            b.LeaseExpiresAtUtc
        });

    return Results.Ok(result);
});

app.MapPost("/api/parking/allocations", async (
    AllocateSpotApiRequest request,
    IParkingAllocationService allocationService,
    CancellationToken ct) =>
{
    var ttl = request.LeaseSeconds is > 0
        ? TimeSpan.FromSeconds(request.LeaseSeconds.Value)
        : (TimeSpan?)null;

    var result = await allocationService.AllocateAsync(
        new AllocateSpotRequest(request.GateId, request.VehicleSessionId, request.VehicleType, ttl),
        ct);

    if (!result.Succeeded)
    {
        return Results.Conflict(new
        {
            error = result.FailureReason,
            computationTimeMs = result.ComputationTimeMs
        });
    }

    return Results.Created(
        $"/api/parking/allocations/{result.Session!.AssignedBayId}",
        new
        {
            result.Session,
            result.ComputationTimeMs
        });
});

app.MapDelete("/api/parking/allocations/{bayId}", async (
    string bayId,
    [FromQuery] long fencingToken,
    IParkingAllocationService allocationService,
    CancellationToken ct) =>
{
    var released = await allocationService.ReleaseLeaseAsync(bayId, fencingToken, ct);
    return released ? Results.NoContent() : Results.Conflict(new { error = "Invalid or expired fencing token." });
});

app.MapPost("/api/parking/bays/{bayId}/occupancy", async (
    string bayId,
    PhysicalOccupancyApiRequest request,
    IParkingAllocationService allocationService,
    CancellationToken ct) =>
{
    try
    {
        var result = await allocationService.ReconcilePhysicalOccupancyAsync(
            bayId,
            request.Occupied,
            request.OccupantSessionId,
            ct);

        return Results.Ok(result);
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound(new { error = $"Bay '{bayId}' not found." });
    }
}).AddEndpointFilter<ApiKeyEndpointFilter>();

app.MapGet("/api/parking/sessions", (IParkingAllocationService allocationService) =>
    Results.Ok(allocationService.GetActiveSessions()));

app.MapPost("/api/parking/sessions/{sessionId}/progress", async (
    string sessionId,
    VehicleProgressApiRequest request,
    IParkingAllocationService allocationService,
    CancellationToken ct) =>
{
    var updated = await allocationService.UpdateVehicleProgressAsync(sessionId, request.CurrentNodeId, ct);
    return updated ? Results.Ok(new { sessionId, request.CurrentNodeId }) : Results.NotFound();
}).AddEndpointFilter<ApiKeyEndpointFilter>();

app.MapGet("/api/parking/telemetry/metrics", (ITelemetryIngestionChannel channel) =>
    Results.Ok(channel.GetMetrics()));

app.MapPost("/api/parking/telemetry/readings", (
    BaySensorReading reading,
    ITelemetryIngestionChannel channel) =>
{
    var enqueued = channel.TryEnqueue(reading);
    return enqueued ? Results.Accepted() : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
}).AddEndpointFilter<ApiKeyEndpointFilter>();

var chaosIntruderEndpoint = app.MapPost("/api/parking/chaos/intruder", async (
    IParkingAllocationService allocationService,
    CancellationToken ct) =>
{
    var targetSession = allocationService.GetActiveSessions().FirstOrDefault();
    if (targetSession is null)
        return Results.Conflict(new { error = "No active leased bay available for intrusion." });

    var result = await allocationService.ReconcilePhysicalOccupancyAsync(
        targetSession.AssignedBayId,
        occupied: true,
        occupantSessionId: $"INTRUDER-{Random.Shared.Next(100, 999)}",
        ct);

    return Results.Ok(result);
});

var chaosBurstEndpoint = app.MapPost("/api/parking/chaos/burst", async (
    [FromQuery] int? count,
    IParkingAllocationService allocationService,
    CancellationToken ct) =>
{
    var batchSize = Math.Clamp(count ?? 25, 1, 60);
    var vehicleTypes = Enum.GetValues<VehicleType>();
    var gates = allocationService.Graph.Gates.Select(g => g.Id).ToArray();

    var tasks = Enumerable.Range(0, batchSize).Select(i =>
    {
        var gate = gates[i % gates.Length];
        var type = vehicleTypes[i % vehicleTypes.Length];
        var sessionId = $"BURST-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{i:D2}";
        return allocationService.AllocateAsync(new AllocateSpotRequest(gate, sessionId, type), ct);
    });

    var results = await Task.WhenAll(tasks);
    var succeeded = results.Count(r => r.Succeeded);

    return Results.Ok(new
    {
        requested = batchSize,
        succeeded,
        failed = batchSize - succeeded,
        sessions = results.Where(r => r.Succeeded && r.Session is not null).Select(r => r.Session)
    });
});

chaosIntruderEndpoint.RequireRateLimiting("chaos");
chaosBurstEndpoint.RequireRateLimiting("chaos");

app.MapHub<ParkingHub>("/hubs/parking");

app.Run();

static string? ResolveWebDirectory(string contentRootPath)
{
    var direct = Path.Combine(contentRootPath, "web");
    if (Directory.Exists(direct))
        return direct;

    var repoRootCandidate = Path.GetFullPath(Path.Combine(contentRootPath, "..", "..", "web"));
    return Directory.Exists(repoRootCandidate) ? repoRootCandidate : null;
}

public sealed record AllocateSpotApiRequest(
    string GateId,
    string VehicleSessionId,
    VehicleType VehicleType,
    int? LeaseSeconds = null);

public sealed record PhysicalOccupancyApiRequest(
    bool Occupied,
    string? OccupantSessionId = null);

public sealed record VehicleProgressApiRequest(string CurrentNodeId);

public partial class Program;
