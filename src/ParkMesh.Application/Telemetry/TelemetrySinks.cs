using ParkMesh.Application.Allocation;
using ParkMesh.Domain;

namespace ParkMesh.Application.Telemetry;

public interface ILedPanelPublisher
{
    Task PublishDirectivesAsync(IReadOnlyList<LedDirective> directives, CancellationToken ct = default);
}

public sealed class NullLedPanelPublisher : ILedPanelPublisher
{
    public static readonly NullLedPanelPublisher Instance = new();

    public Task PublishDirectivesAsync(IReadOnlyList<LedDirective> directives, CancellationToken ct = default) =>
        Task.CompletedTask;
}

public interface IParkingEventSink
{
    ValueTask OnBayAllocatedAsync(ActiveVehicleSession session, CancellationToken ct = default);
    ValueTask OnRouteReassignedAsync(string displacedBayId, string displacedSessionId, ActiveVehicleSession? newSession, CancellationToken ct = default);
    ValueTask OnPhysicalOccupancyChangedAsync(string bayId, BayStatus newStatus, CancellationToken ct = default);
    ValueTask OnVehicleMovedAsync(string vehicleSessionId, string currentNodeId, CancellationToken ct = default);
}

public sealed class NullParkingEventSink : IParkingEventSink
{
    public static readonly NullParkingEventSink Instance = new();

    public ValueTask OnBayAllocatedAsync(ActiveVehicleSession session, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask OnRouteReassignedAsync(string displacedBayId, string displacedSessionId, ActiveVehicleSession? newSession, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask OnPhysicalOccupancyChangedAsync(string bayId, BayStatus newStatus, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask OnVehicleMovedAsync(string vehicleSessionId, string currentNodeId, CancellationToken ct = default) => ValueTask.CompletedTask;
}
