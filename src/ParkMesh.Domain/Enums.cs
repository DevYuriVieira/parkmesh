namespace ParkMesh.Domain;

public enum VehicleType
{
    Standard = 0,
    EV = 1,
    SUV = 2,
    Pcd = 3,
    Moto = 4
}

public enum BayStatus
{
    Free = 0,
    Leased = 1,
    Occupied = 2,
    OutOfService = 3
}

public enum NodeType
{
    Gate = 0,
    Intersection = 1,
    Ramp = 2,
    Bay = 3
}

public enum DirectionHint
{
    Straight = 0,
    Left = 1,
    Right = 2,
    UpRamp = 3,
    DownRamp = 4,
    Arrive = 5
}
