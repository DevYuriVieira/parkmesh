using ParkMesh.Domain;

namespace ParkMesh.Application.Allocation;

public sealed record AllocateSpotRequest(
    string StartNodeId,
    string VehicleSessionId,
    VehicleType VehicleType,
    TimeSpan? LeaseTtl = null);

public sealed record ActiveVehicleSession(
    string VehicleSessionId,
    VehicleType VehicleType,
    string AssignedBayId,
    long FencingToken,
    ParkingRoute Route,
    string LastKnownNodeId,
    DateTimeOffset AllocatedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record AllocateSpotResult(
    bool Succeeded,
    ActiveVehicleSession? Session = null,
    string? FailureReason = null,
    double ComputationTimeMs = 0.0);

public sealed record PhysicalOccupancyResult(
    string BayId,
    BayStatus NewStatus,
    bool WasIntruder,
    string? DisplacedSessionId,
    ActiveVehicleSession? ReroutedSession);

public sealed record ExpiredLeaseInfo(
    string BayId,
    string VehicleSessionId,
    long FencingToken,
    DateTimeOffset ExpiredAtUtc);
