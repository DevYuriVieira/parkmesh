using Microsoft.Extensions.Logging.Abstractions;
using ParkMesh.Application.Abstractions;
using ParkMesh.Application.Allocation;
using ParkMesh.Application.Audit;
using ParkMesh.Application.Routing;
using ParkMesh.Domain;
using ParkMesh.Infrastructure.Audit;
using ParkMesh.Infrastructure.Redis;

namespace ParkMesh.UnitTests;

public sealed class SecurityAndRlsTests
{
    [Theory]
    [InlineData("default")]
    [InlineData("tenant-a")]
    [InlineData("garage_sp_01")]
    [InlineData("A")]
    public void TenantIdValidator_AcceptsWellFormedIdentifiers(string tenantId)
    {
        Assert.True(TenantIdValidator.IsValid(tenantId));
        Assert.Equal(tenantId, TenantIdValidator.NormalizeOrThrow(tenantId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-leading-dash")]
    [InlineData("_leading_underscore")]
    [InlineData("tenant with spaces")]
    [InlineData("tenant/traversal")]
    [InlineData("../etc/passwd")]
    [InlineData("tenant'; DROP TABLE parking_audit_events;--")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 65 chars
    public void TenantIdValidator_RejectsMalformedOrInjectionInputs(string tenantId)
    {
        Assert.False(TenantIdValidator.IsValid(tenantId));
        Assert.Throws<ArgumentException>(() => TenantIdValidator.NormalizeOrThrow(tenantId));
    }

    [Fact]
    public async Task AsyncLocalTenantContext_IsolatesConcurrentTasksAndRestoresScopeOnDispose()
    {
        var context = new AsyncLocalTenantContext();
        Assert.Equal(TenantDefaults.DefaultTenantId, context.CurrentTenantId);

        using (context.BeginScope("tenant-outer"))
        {
            Assert.Equal("tenant-outer", context.CurrentTenantId);

            var observed = await Task.WhenAll(
                RunScopedAsync(context, "tenant-alpha"),
                RunScopedAsync(context, "tenant-beta"),
                RunScopedAsync(context, "tenant-gamma"));

            Assert.Equal(["tenant-alpha", "tenant-beta", "tenant-gamma"], observed);
            Assert.Equal("tenant-outer", context.CurrentTenantId);
        }

        Assert.Equal(TenantDefaults.DefaultTenantId, context.CurrentTenantId);
    }

    private static async Task<string> RunScopedAsync(ITenantContext context, string tenantId)
    {
        using var _ = context.BeginScope(tenantId);
        await Task.Yield();
        return context.CurrentTenantId;
    }

    [Fact]
    public async Task InMemoryAuditStore_IsolatesEventsAndPhysicalStatesPerTenantFailClosed()
    {
        var store = new InMemoryParkingAuditStore();
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;

        await store.RecordBatchAsync(
        [
            new ParkingAuditEvent(now, AuditEventType.BayAllocated, "F0-B01", "S-A", 1, VehicleType.Standard, "GATE-N1", "a", TenantId: "tenant-alpha"),
            new ParkingAuditEvent(now.AddSeconds(1), AuditEventType.PhysicalOccupied, "F0-B01", "S-A", null, null, null, null, TenantId: "tenant-alpha"),
            new ParkingAuditEvent(now.AddSeconds(2), AuditEventType.BayOutOfService, "F0-B01", null, null, null, null, "maint", TenantId: "tenant-beta"),
            new ParkingAuditEvent(now.AddSeconds(3), AuditEventType.BayAllocated, "F0-B02", "S-B", 1, VehicleType.EV, "GATE-S1", "b", TenantId: "tenant-beta")
        ]);

        var alphaEvents = await store.GetRecentAsync("tenant-alpha", 50);
        var betaEvents = await store.GetRecentAsync("tenant-beta", 50);
        var defaultEvents = await store.GetRecentAsync("default", 50);
        var invalidEvents = await store.GetRecentAsync("'; DROP TABLE--", 50);

        Assert.Equal(2, alphaEvents.Count);
        Assert.All(alphaEvents, e => Assert.Equal("tenant-alpha", e.TenantId));

        Assert.Equal(2, betaEvents.Count);
        Assert.All(betaEvents, e => Assert.Equal("tenant-beta", e.TenantId));

        Assert.Empty(defaultEvents);
        Assert.Empty(invalidEvents);

        var alphaStates = await store.GetLatestBayPhysicalStatesAsync("tenant-alpha");
        var betaStates = await store.GetLatestBayPhysicalStatesAsync("tenant-beta");

        Assert.Equal(AuditEventType.PhysicalOccupied, alphaStates["F0-B01"]);
        Assert.Equal(AuditEventType.BayOutOfService, betaStates["F0-B01"]);
        Assert.False((await store.GetLatestBayPhysicalStatesAsync("invalid tenant")).Any());
    }

    [Fact]
    public async Task ParkingAllocationService_StampsCurrentTenantOnAuditEventsAndRehydratesPerTenant()
    {
        var tenantContext = new AsyncLocalTenantContext();
        var auditSink = new AuditChannelSink();
        var store = new InMemoryParkingAuditStore();
        await store.InitializeAsync();
        var leaseManager = new InMemoryBayLeaseManager();
        var graph = StandardTopologyFactory.CreateStandardGarage();

        var service = new ParkingAllocationService(
            graph,
            new AStarPathfinder(),
            leaseManager,
            new InMemoryEdgeCongestionTracker(),
            NullLogger<ParkingAllocationService>.Instance,
            auditSink: auditSink,
            auditStore: store,
            tenantContext: tenantContext);

        string leasedBay;
        using (tenantContext.BeginScope("garage-sp-01"))
        {
            var alloc = await service.AllocateAsync(new AllocateSpotRequest("GATE-N1", "CAR-SP-1", VehicleType.Standard));
            Assert.True(alloc.Succeeded);
            leasedBay = alloc.Session!.AssignedBayId;
            await service.SetBayMaintenanceAsync("F0-B05", outOfService: true);
        }

        var events = new List<ParkingAuditEvent>();
        while (auditSink.TryRead(out var ev))
            events.Add(ev);

        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal("garage-sp-01", e.TenantId));
        await store.RecordBatchAsync(events);

        var recoveredService = new ParkingAllocationService(
            StandardTopologyFactory.CreateStandardGarage(),
            new AStarPathfinder(),
            new InMemoryBayLeaseManager(),
            new InMemoryEdgeCongestionTracker(),
            NullLogger<ParkingAllocationService>.Instance,
            auditStore: store,
            tenantContext: tenantContext);

        using (tenantContext.BeginScope("garage-rj-02"))
        {
            var rjRestored = await recoveredService.RehydrateStateAsync();
            Assert.Equal(0, rjRestored);
        }

        using (tenantContext.BeginScope("garage-sp-01"))
        {
            var spRestored = await recoveredService.RehydrateStateAsync();
            Assert.Equal(1, spRestored);
            Assert.Equal(BayStatus.OutOfService, recoveredService.Graph.BaysById["F0-B05"].Status);
            Assert.Equal(BayStatus.Free, recoveredService.Graph.BaysById[leasedBay].Status);
        }
    }

    [Fact]
    public void PostgresParkingAuditStore_GuardsConnectionStringAndRoleIdentifier()
    {
        Assert.ThrowsAny<ArgumentException>(() => new PostgresParkingAuditStore("   "));
        Assert.Throws<ArgumentException>(() => new PostgresParkingAuditStore("Host=localhost;Database=test", "bad_role; DROP TABLE x;--"));
        Assert.Throws<ArgumentException>(() => new PostgresParkingAuditStore("Host=localhost;Database=test", "1invalid"));
    }

    [Fact]
    public async Task InMemoryAuditStore_ClampsQueryLimitsAndResolvesLatestChronologicalPhysicalState()
    {
        var store = new InMemoryParkingAuditStore();
        await store.InitializeAsync();
        var t0 = DateTimeOffset.UtcNow;

        await store.RecordBatchAsync(
        [
            new ParkingAuditEvent(t0, AuditEventType.BayAllocated, "F0-B09", "S-1", 1, VehicleType.Standard, "GATE-N1", null, TenantId: "tenant-alpha"),
            new ParkingAuditEvent(t0.AddSeconds(1), AuditEventType.PhysicalOccupied, "F0-B09", "S-1", null, null, null, null, TenantId: "tenant-alpha"),
            new ParkingAuditEvent(t0.AddSeconds(2), AuditEventType.PhysicalVacated, "F0-B09", "S-1", null, null, null, null, TenantId: "tenant-alpha"),
            new ParkingAuditEvent(t0.AddSeconds(3), AuditEventType.BayOutOfService, "F0-B09", null, null, null, null, "maint", TenantId: "tenant-alpha"),
            new ParkingAuditEvent(t0.AddSeconds(4), AuditEventType.BayRestored, "F0-B09", null, null, null, null, "cleared", TenantId: "tenant-alpha")
        ]);

        var latest = await store.GetLatestBayPhysicalStatesAsync("tenant-alpha");
        Assert.Equal(AuditEventType.BayRestored, latest["F0-B09"]);

        Assert.Equal(2, (await store.GetRecentAsync("tenant-alpha", limit: 2)).Count);
        Assert.Single(await store.GetRecentAsync("tenant-alpha", limit: 0));
    }
}
