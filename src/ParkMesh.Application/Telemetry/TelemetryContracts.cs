using ParkMesh.Domain;

namespace ParkMesh.Application.Telemetry;

public static class MqttTopics
{
    public const string SensorsWildcard = "parkmesh/sensors/+/+/state";
    public const string GatesWildcard = "parkmesh/gates/+/admitted";
    public const string PanelsWildcard = "parkmesh/panels/+/direction";

    public static string SensorState(int floor, string bayId) => $"parkmesh/sensors/{floor}/{bayId}/state";
    public static string GateAdmitted(string gateId) => $"parkmesh/gates/{gateId}/admitted";
    public static string PanelDirection(string panelId) => $"parkmesh/panels/{panelId}/direction";
}

public sealed record BaySensorReading(
    string BayId,
    bool Occupied,
    double DistanceCm,
    long Seq,
    DateTimeOffset TimestampUtc,
    string? VehicleSessionId = null,
    string? TenantId = null);

public sealed record VehicleAdmittedEvent(
    string EventId,
    string GateId,
    VehicleType VehicleType,
    string? PlateHash,
    DateTimeOffset TimestampUtc,
    string? VehicleSessionId = null);

public sealed record LedPanelCommand(
    string PanelId,
    int Floor,
    DirectionHint Direction,
    DateTimeOffset TimestampUtc);

public sealed class SensorDebounceOptions
{
    public const string SectionName = "SensorDebounce";
    public int MinConsecutiveReadings { get; set; } = 2;
    public int StabilityWindowMs { get; set; } = 1500;
    public int ChannelCapacity { get; set; } = 10_000;
}
