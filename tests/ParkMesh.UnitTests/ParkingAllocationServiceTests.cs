using Microsoft.Extensions.Logging.Abstractions;
using ParkMesh.Application.Abstractions;
using ParkMesh.Application.Allocation;
using ParkMesh.Application.Routing;
using ParkMesh.Domain;

namespace ParkMesh.UnitTests;

public sealed class ParkingAllocationServiceTests
{
    [Fact]
    public async Task AllocateAndRelease_UpdatesBayStatusCongestionAndEnforcesFencingToken()
    {
        var (service, graph, congestion) = CreateService();

        var allocation = await service.AllocateAsync(
            new AllocateSpotRequest("GATE-N1", "SES-001", VehicleType.EV));

        Assert.True(allocation.Succeeded);
        Assert.NotNull(allocation.Session);
        var bay = graph.BaysById[allocation.Session.AssignedBayId];
        Assert.Equal(BayStatus.Leased, bay.Status);
        Assert.Equal(VehicleType.EV, bay.PrimaryType);
        Assert.Contains(congestion.Snapshot(), kvp => kvp.Value > 0);

        var wrongRelease = await service.ReleaseLeaseAsync(bay.BayId, allocation.Session.FencingToken + 99);
        Assert.False(wrongRelease);
        Assert.Equal(BayStatus.Leased, bay.Status);

        var validRelease = await service.ReleaseLeaseAsync(bay.BayId, allocation.Session.FencingToken);
        Assert.True(validRelease);
        Assert.Equal(BayStatus.Free, bay.Status);
        Assert.All(congestion.Snapshot().Values, penalty => Assert.Equal(0.0, penalty));
    }

    [Fact]
    public async Task ReconcilePhysicalOccupancy_WhenIntruderTakesLeasedBay_ReroutesDisplacedVehicleLive()
    {
        var (service, graph, _) = CreateService();

        var initial = await service.AllocateAsync(
            new AllocateSpotRequest("GATE-N1", "SES-V1", VehicleType.Standard));

        Assert.True(initial.Succeeded);
        var originalBayId = initial.Session!.AssignedBayId;

        Assert.True(service.UpdateVehicleProgress("SES-V1", "F0-I-W-C"));

        var reconciliation = await service.ReconcilePhysicalOccupancyAsync(
            originalBayId,
            occupied: true,
            occupantSessionId: "INTRUDER-99");

        Assert.True(reconciliation.WasIntruder);
        Assert.Equal("SES-V1", reconciliation.DisplacedSessionId);
        Assert.Equal(BayStatus.Occupied, graph.BaysById[originalBayId].Status);
        Assert.NotNull(reconciliation.ReroutedSession);
        Assert.NotEqual(originalBayId, reconciliation.ReroutedSession.AssignedBayId);
        Assert.Equal("F0-I-W-C", reconciliation.ReroutedSession.Route.NodeIds[0]);
        Assert.Equal(BayStatus.Leased, graph.BaysById[reconciliation.ReroutedSession.AssignedBayId].Status);
    }

    [Fact]
    public async Task AllocateAsync_UnderParallelContention_NeverDoubleBooksAnyBay()
    {
        var (service, _, _) = CreateService();

        var tasks = Enumerable.Range(1, 30)
            .Select(i => service.AllocateAsync(
                new AllocateSpotRequest("GATE-N1", $"CAR-{i:D2}", VehicleType.Standard)))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.True(r.Succeeded, r.FailureReason));
        var assignedBays = results.Select(r => r.Session!.AssignedBayId).ToList();
        Assert.Equal(30, assignedBays.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    private static (ParkingAllocationService Service, ParkingGraph Graph, IEdgeCongestionTracker Congestion) CreateService()
    {
        var graph = StandardTopologyFactory.CreateStandardGarage();
        var pathfinder = new AStarPathfinder();
        var leaseManager = new FakeBayLeaseManager();
        var congestion = new InMemoryEdgeCongestionTracker();
        var service = new ParkingAllocationService(
            graph,
            pathfinder,
            leaseManager,
            congestion,
            NullLogger<ParkingAllocationService>.Instance);

        return (service, graph, congestion);
    }

    private sealed class FakeBayLeaseManager : IBayLeaseManager
    {
        private readonly Dictionary<string, (string SessionId, long Token)> _leases = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _fences = new(StringComparer.OrdinalIgnoreCase);
        private readonly Lock _lock = new();

        public Task<LeaseAcquireResult> TryAcquireLeaseAsync(string bayId, string vehicleSessionId, TimeSpan ttl, CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (_leases.ContainsKey(bayId))
                    return Task.FromResult(new LeaseAcquireResult(false, 0, null, "Already leased"));

                var token = _fences.GetValueOrDefault(bayId) + 1;
                _fences[bayId] = token;
                _leases[bayId] = (vehicleSessionId, token);
                return Task.FromResult(new LeaseAcquireResult(true, token, DateTimeOffset.UtcNow.Add(ttl)));
            }
        }

        public Task<bool> TryReleaseLeaseAsync(string bayId, long fencingToken, CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (_leases.TryGetValue(bayId, out var active) && active.Token == fencingToken)
                {
                    _leases.Remove(bayId);
                    return Task.FromResult(true);
                }

                return Task.FromResult(false);
            }
        }

        public Task<bool> InvalidateLeaseAsync(string bayId, CancellationToken ct = default)
        {
            lock (_lock)
            {
                return Task.FromResult(_leases.Remove(bayId));
            }
        }

        public Task<string?> GetActiveLeaseHolderAsync(string bayId, CancellationToken ct = default)
        {
            lock (_lock)
            {
                return Task.FromResult(_leases.TryGetValue(bayId, out var active) ? active.SessionId : null);
            }
        }
    }
}
