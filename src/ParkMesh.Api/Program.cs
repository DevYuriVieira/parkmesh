using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using ParkMesh.Application.Allocation;
using ParkMesh.Domain;
using ParkMesh.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddParkMeshCore();
builder.Services.AddParkMeshInfrastructure(builder.Configuration);

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "ParkMesh.Api" }));

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
});

app.MapGet("/api/parking/sessions", (IParkingAllocationService allocationService) =>
    Results.Ok(allocationService.GetActiveSessions()));

app.Run();

public sealed record AllocateSpotApiRequest(
    string GateId,
    string VehicleSessionId,
    VehicleType VehicleType,
    int? LeaseSeconds = null);

public sealed record PhysicalOccupancyApiRequest(
    bool Occupied,
    string? OccupantSessionId = null);

public partial class Program;
