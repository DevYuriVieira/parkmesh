namespace ParkMesh.Domain;

public sealed class ParkingBay
{
    private readonly HashSet<VehicleType> _allowedTypes;

    public ParkingBay(
        string bayId,
        string nodeId,
        int floor,
        double x,
        double y,
        VehicleType primaryType,
        IEnumerable<VehicleType>? allowedTypes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bayId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);

        BayId = bayId;
        NodeId = nodeId;
        Floor = floor;
        X = x;
        Y = y;
        PrimaryType = primaryType;
        _allowedTypes = allowedTypes is null
            ? [primaryType]
            : [.. allowedTypes, primaryType];
    }

    public string BayId { get; }
    public string NodeId { get; }
    public int Floor { get; }
    public double X { get; }
    public double Y { get; }
    public VehicleType PrimaryType { get; }
    public IReadOnlySet<VehicleType> AllowedTypes => _allowedTypes;

    public BayStatus Status { get; private set; } = BayStatus.Free;
    public string? CurrentSessionId { get; private set; }
    public long? FencingToken { get; private set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; private set; }

    public bool SupportsVehicle(VehicleType vehicleType) => _allowedTypes.Contains(vehicleType);

    public bool TrySetLeased(string vehicleSessionId, long fencingToken, DateTimeOffset expiresAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleSessionId);

        if (Status != BayStatus.Free)
            return false;

        if (FencingToken.HasValue && fencingToken <= FencingToken.Value)
            return false;

        Status = BayStatus.Leased;
        CurrentSessionId = vehicleSessionId;
        FencingToken = fencingToken;
        LeaseExpiresAtUtc = expiresAtUtc;
        return true;
    }

    public bool TryReleaseLease(long fencingToken)
    {
        if (Status != BayStatus.Leased || FencingToken != fencingToken)
            return false;

        Status = BayStatus.Free;
        CurrentSessionId = null;
        LeaseExpiresAtUtc = null;
        return true;
    }

    public void MarkOccupied(string? sessionId = null)
    {
        Status = BayStatus.Occupied;
        CurrentSessionId = sessionId;
        LeaseExpiresAtUtc = null;
    }

    public void MarkFree()
    {
        Status = BayStatus.Free;
        CurrentSessionId = null;
        LeaseExpiresAtUtc = null;
    }

    public void MarkOutOfService()
    {
        Status = BayStatus.OutOfService;
        CurrentSessionId = null;
        LeaseExpiresAtUtc = null;
    }
}
